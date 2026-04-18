using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GodotMonoMcp;

public sealed class GodotTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HashSet<string> HeadlessTools = new(StringComparer.Ordinal)
    {
        "create_scene",
        "add_node",
        "load_sprite",
        "export_mesh_library",
        "save_scene",
        "get_uid",
        "update_project_uids",
        "read_scene",
        "modify_scene_node",
        "remove_scene_node",
        "attach_script",
        "create_resource",
        "manage_resource",
        "manage_scene_signals",
        "manage_theme_resource",
        "manage_scene_structure"
    };

    private readonly object _logLock = new();
    private readonly List<string> _stdoutLogs = [];
    private readonly List<string> _stderrLogs = [];
    private readonly SemaphoreSlim _runtimeLock = new(1, 1);

    private Process? _activeProcess;
    private TcpClient? _runtimeClient;
    private StreamReader? _runtimeReader;
    private StreamWriter? _runtimeWriter;
    private int _runtimePort = 9090;

    public async Task<string> ExecuteAsync(string toolName, Dictionary<string, JsonElement> arguments)
    {
        if (!ToolCatalog.ToolNames.Contains(toolName))
        {
            throw new InvalidOperationException($"Unknown tool: {toolName}");
        }

        switch (toolName)
        {
            case "launch_editor":
                return await LaunchEditor(arguments);
            case "run_project":
                return await RunProject(arguments);
            case "stop_project":
                return await StopProject();
            case "get_debug_output":
                return GetDebugOutput();
            case "get_godot_version":
                return await GetGodotVersion(arguments);
            case "list_projects":
                return ListProjects(arguments);
            case "get_project_info":
                return GetProjectInfo(arguments);
            case "game_get_errors":
                return ReadCapturedErrors();
            case "game_get_logs":
                return ReadCapturedLogs();
            case "read_file":
                return ReadFile(arguments);
            case "write_file":
                return WriteFile(arguments);
            case "delete_file":
                return DeleteFile(arguments);
            case "create_directory":
                return CreateDirectory(arguments);
            case "list_project_files":
                return ListProjectFiles(arguments);
            case "read_project_settings":
                return ReadProjectSettings(arguments);
            case "modify_project_settings":
                return ModifyProjectSettings(arguments);
            case "create_project":
                return CreateProject(arguments);
            case "rename_file":
                return RenameFile(arguments);
            case "set_main_scene":
                return SetMainScene(arguments);
            case "manage_autoloads":
                return ManageAutoloads(arguments);
            case "manage_input_map":
                return ManageInputMap(arguments);
            case "manage_export_presets":
                return ManageExportPresets(arguments);
            case "manage_layers":
                return ManageLayers(arguments);
            case "manage_plugins":
                return ManagePlugins(arguments);
            case "manage_shader":
                return ManageShader(arguments);
            case "manage_translations":
                return ManageTranslations(arguments);
            case "create_script":
                return CreateScript(arguments);
            case "export_project":
                return await ExportProject(arguments);
            case "manage_ci_pipeline":
                return ManageCiPipeline(arguments);
            case "manage_docker_export":
                return ManageDockerExport(arguments);
        }

        if (toolName.StartsWith("game_", StringComparison.Ordinal))
        {
            return await ExecuteRuntimeTool(toolName, arguments);
        }

        if (HeadlessTools.Contains(toolName))
        {
            return await ExecuteHeadlessTool(toolName, arguments);
        }

        throw new InvalidOperationException($"Tool mapped but not implemented: {toolName}");
    }

    private async Task<string> LaunchEditor(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        EnsureGodotProject(projectPath);
        var godotPath = await DetectGodotPath(args);

        var psi = new ProcessStartInfo
        {
            FileName = godotPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(projectPath);

        var process = Process.Start(psi);
        return JsonSerializer.Serialize(new
        {
            success = process is not null,
            pid = process?.Id,
            godotPath,
            projectPath
        }, JsonOptions);
    }

    private async Task<string> RunProject(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        EnsureGodotProject(projectPath);

        await StopProject();

        var godotPath = await DetectGodotPath(args);
        EnsureRuntimeScriptInstalled(projectPath);

        var psi = new ProcessStartInfo
        {
            FileName = godotPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        _runtimePort = GetAvailableLocalPort();
        psi.Environment["GODOT_MCP_PORT"] = _runtimePort.ToString();

        psi.ArgumentList.Add("--path");
        psi.ArgumentList.Add(projectPath);

        var scene = ReadStringAny(args, false, "scene", "scenePath");
        if (!string.IsNullOrWhiteSpace(scene))
        {
            psi.ArgumentList.Add(scene);
        }

        _activeProcess = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Godot process");
        _activeProcess.OutputDataReceived += (_, e) => CaptureLog(_stdoutLogs, e.Data);
        _activeProcess.ErrorDataReceived += (_, e) => CaptureLog(_stderrLogs, e.Data);
        _activeProcess.BeginOutputReadLine();
        _activeProcess.BeginErrorReadLine();

        var runtimeConnected = await ConnectRuntimeAsync(TimeSpan.FromSeconds(18));

        return JsonSerializer.Serialize(new
        {
            success = true,
            pid = _activeProcess.Id,
            projectPath,
            godotPath,
            runtimeConnected,
            runtimePort = _runtimePort,
            mono = true,
            godotMonoTarget = "4.6.2"
        }, JsonOptions);
    }

    private async Task<string> StopProject()
    {
        DisposeRuntimeConnection();

        if (_activeProcess is null)
        {
            return JsonSerializer.Serialize(new { success = true, running = false }, JsonOptions);
        }

        try
        {
            if (!_activeProcess.HasExited)
            {
                _activeProcess.Kill(entireProcessTree: true);
                await _activeProcess.WaitForExitAsync();
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _activeProcess.Dispose();
            _activeProcess = null;
        }

        return JsonSerializer.Serialize(new { success = true, running = false }, JsonOptions);
    }

    private string GetDebugOutput()
    {
        lock (_logLock)
        {
            return JsonSerializer.Serialize(new
            {
                processRunning = _activeProcess is { HasExited: false },
                runtimeConnected = _runtimeClient?.Connected == true,
                stdout = _stdoutLogs.ToArray(),
                stderr = _stderrLogs.ToArray()
            }, JsonOptions);
        }
    }

    private async Task<string> GetGodotVersion(Dictionary<string, JsonElement> args)
    {
        var path = await DetectGodotPath(args);
        var (code, stdout, stderr) = await RunProcess(path, ["--version"]);

        return JsonSerializer.Serialize(new
        {
            path,
            exitCode = code,
            version = (stdout + "\n" + stderr).Trim()
        }, JsonOptions);
    }

    private string ListProjects(Dictionary<string, JsonElement> args)
    {
        var root = Path.GetFullPath(ReadStringAny(args, true, "directory")!);
        var recursive = ReadBoolAny(args, false, "recursive");

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var projects = Directory.EnumerateFiles(root, "project.godot", option)
            .Select(file => Path.GetDirectoryName(file)!)
            .Select(path => new { name = Path.GetFileName(path), path })
            .OrderBy(x => x.path, StringComparer.Ordinal)
            .ToArray();

        return JsonSerializer.Serialize(new { root, count = projects.Length, projects }, JsonOptions);
    }

    private string GetProjectInfo(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        EnsureGodotProject(projectPath);

        var settings = ReadGodotSettingsFile(Path.Combine(projectPath, "project.godot"));
        var csprojFiles = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToArray();

        var slnFiles = Directory.EnumerateFiles(projectPath, "*.sln", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(x => x)
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            projectPath,
            projectName = settings.TryGetValue("application/config/name", out var n) ? TrimQuoted(n) : null,
            mainScene = settings.TryGetValue("application/run/main_scene", out var m) ? TrimQuoted(m) : null,
            features = settings.TryGetValue("application/config/features", out var f) ? f : null,
            csprojFiles,
            slnFiles,
            hasMonoArtifacts = Directory.Exists(Path.Combine(projectPath, ".godot", "mono")),
            csharpScriptCount = Directory.EnumerateFiles(projectPath, "*.cs", SearchOption.AllDirectories).Count(),
            sceneCount = Directory.EnumerateFiles(projectPath, "*.tscn", SearchOption.AllDirectories).Count()
        }, JsonOptions);
    }

    private async Task<string> ExecuteRuntimeTool(string toolName, Dictionary<string, JsonElement> args)
    {
        if (_runtimeClient?.Connected != true)
        {
            var connected = await ConnectRuntimeAsync(TimeSpan.FromSeconds(4));
            if (!connected)
            {
                throw new InvalidOperationException("Runtime TCP is not connected. Call run_project first.");
            }
        }

        var payload = new
        {
            command = RuntimeCommandForTool(toolName),
            @params = ConvertArgumentsToPlainObject(args, snakeCaseKeys: true)
        };

        var message = JsonSerializer.Serialize(payload, JsonOptions);

        await _runtimeLock.WaitAsync();
        try
        {
            await _runtimeWriter!.WriteLineAsync(message);
            var line = await _runtimeReader!.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line))
            {
                throw new InvalidOperationException("Runtime server returned empty response");
            }

            return line;
        }
        finally
        {
            _runtimeLock.Release();
        }
    }

    private async Task<string> ExecuteHeadlessTool(string toolName, Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        EnsureGodotProject(projectPath);

        var godotPath = await DetectGodotPath(args);
        var scriptPath = GetAssetPath("godot_operations.gd");
        var operation = HeadlessOperationForTool(toolName);

        var payload = ConvertArgumentsToPlainObject(args, snakeCaseKeys: true);
        payload["project_path"] = projectPath;

        var argsList = new List<string>
        {
            "--headless",
            "--path", projectPath,
            "--script", scriptPath,
            operation,
            JsonSerializer.Serialize(payload, JsonOptions)
        };

        var (code, stdout, stderr) = await RunProcess(godotPath, argsList);

        return JsonSerializer.Serialize(new
        {
            tool = toolName,
            operation,
            exitCode = code,
            stdout,
            stderr
        }, JsonOptions);
    }

    private string ReadCapturedErrors()
    {
        lock (_logLock)
        {
            return JsonSerializer.Serialize(new { errors = _stderrLogs.ToArray() }, JsonOptions);
        }
    }

    private string ReadCapturedLogs()
    {
        lock (_logLock)
        {
            return JsonSerializer.Serialize(new { logs = _stdoutLogs.ToArray() }, JsonOptions);
        }
    }

    private string ReadFile(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var filePath = ResolvePathInProject(projectPath, ReadStringAny(args, true, "filePath", "path")!);

        return JsonSerializer.Serialize(new
        {
            filePath,
            content = File.ReadAllText(filePath)
        }, JsonOptions);
    }

    private string WriteFile(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var filePath = ResolvePathInProject(projectPath, ReadStringAny(args, true, "filePath", "path")!);
        var content = ReadStringAny(args, true, "content")!;

        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        File.WriteAllText(filePath, content, new UTF8Encoding(false));

        return JsonSerializer.Serialize(new { success = true, filePath }, JsonOptions);
    }

    private string DeleteFile(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var filePath = ResolvePathInProject(projectPath, ReadStringAny(args, true, "filePath", "path")!);

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return JsonSerializer.Serialize(new { success = true, filePath }, JsonOptions);
    }

    private string CreateDirectory(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var directoryPath = ResolvePathInProject(projectPath, ReadStringAny(args, true, "directoryPath", "path")!);
        Directory.CreateDirectory(directoryPath);

        return JsonSerializer.Serialize(new { success = true, directoryPath }, JsonOptions);
    }

    private string ListProjectFiles(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var extension = ReadStringAny(args, false, "extension");

        var files = Directory.EnumerateFiles(projectPath, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => string.IsNullOrWhiteSpace(extension) || path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(projectPath, path).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        return JsonSerializer.Serialize(new { projectPath, count = files.Length, files }, JsonOptions);
    }

    private string ReadProjectSettings(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var settings = ReadGodotSettingsFile(Path.Combine(projectPath, "project.godot"));

        return JsonSerializer.Serialize(new { projectPath, settings }, JsonOptions);
    }

    private string ModifyProjectSettings(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var key = ReadStringAny(args, true, "key")!;
        var value = ReadStringAny(args, true, "value")!;

        UpsertProjectSetting(Path.Combine(projectPath, "project.godot"), key, value);
        return JsonSerializer.Serialize(new { success = true, key, value }, JsonOptions);
    }

    private string CreateProject(Dictionary<string, JsonElement> args)
    {
        var projectPath = Path.GetFullPath(ReadStringAny(args, true, "projectPath")!);
        var projectName = ReadStringAny(args, false, "projectName") ?? Path.GetFileName(projectPath);
        var namespaceName = ReadStringAny(args, false, "namespace") ?? SanitizeIdentifier(projectName);

        Directory.CreateDirectory(projectPath);

        var projectFile = Path.Combine(projectPath, "project.godot");
        if (!File.Exists(projectFile))
        {
            var projectText = """
config_version=5

[application]
config/name="__NAME__"
config/features=PackedStringArray("4.6", "C#", "Mono")
run/main_scene=""

[dotnet]
project/assembly_name="__NAME__"

[rendering]
renderer/rendering_method="forward_plus"
""".Replace("__NAME__", projectName, StringComparison.Ordinal);

            File.WriteAllText(projectFile, projectText, new UTF8Encoding(false));
        }

        var csprojFile = Path.Combine(projectPath, projectName + ".csproj");
        if (!File.Exists(csprojFile))
        {
            var csprojText =
                "<Project Sdk=\"Godot.NET.Sdk/4.6.2\">\n" +
                "  <PropertyGroup>\n" +
                "    <TargetFramework>net8.0</TargetFramework>\n" +
                $"    <RootNamespace>{namespaceName}</RootNamespace>\n" +
                "    <Nullable>enable</Nullable>\n" +
                "  </PropertyGroup>\n" +
                "</Project>\n";
            File.WriteAllText(csprojFile, csprojText, new UTF8Encoding(false));
        }

        var solutionFile = Path.Combine(projectPath, projectName + ".sln");
        if (!File.Exists(solutionFile))
        {
            var projectGuid = Guid.NewGuid().ToString().ToUpperInvariant();
            var slnText =
                "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
                "# Visual Studio Version 17\n" +
                "VisualStudioVersion = 17.0.31903.59\n" +
                "MinimumVisualStudioVersion = 10.0.40219.1\n" +
                $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{projectName}.csproj\", \"{{{projectGuid}}}\"\n" +
                "EndProject\n" +
                "Global\n" +
                "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n" +
                "\t\tDebug|Any CPU = Debug|Any CPU\n" +
                "\t\tRelease|Any CPU = Release|Any CPU\n" +
                "\tEndGlobalSection\n" +
                "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n" +
                "\tEndGlobalSection\n" +
                "EndGlobal\n";
            File.WriteAllText(solutionFile, slnText, new UTF8Encoding(false));
        }

        return JsonSerializer.Serialize(new
        {
            success = true,
            projectPath,
            projectFile,
            csprojFile,
            solutionFile,
            mono = true,
            godotMonoTarget = "4.6.2"
        }, JsonOptions);
    }

    private string RenameFile(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var source = ReadStringAny(args, false, "from", "filePath", "sourcePath");
        var target = ReadStringAny(args, false, "to", "newPath", "targetPath");

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
        {
            throw new InvalidOperationException("rename_file requires from/to or filePath/newPath");
        }

        var from = ResolvePathInProject(projectPath, source);
        var to = ResolvePathInProject(projectPath, target);

        Directory.CreateDirectory(Path.GetDirectoryName(to)!);

        if (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
        else if (Directory.Exists(from))
        {
            if (Directory.Exists(to))
            {
                Directory.Delete(to, recursive: true);
            }

            Directory.Move(from, to);
        }
        else
        {
            throw new FileNotFoundException("Path not found", from);
        }

        return JsonSerializer.Serialize(new { success = true, from, to }, JsonOptions);
    }

    private string SetMainScene(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var scene = ReadStringAny(args, true, "mainScene", "scenePath")!;
        var value = scene.StartsWith("res://", StringComparison.Ordinal) ? scene : "res://" + scene.TrimStart('/');

        UpsertProjectSetting(Path.Combine(projectPath, "project.godot"), "application/run/main_scene", '"' + value + '"');
        return JsonSerializer.Serialize(new { success = true, mainScene = value }, JsonOptions);
    }

    private string ManageAutoloads(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "list").ToLowerInvariant();
        var projectFile = Path.Combine(projectPath, "project.godot");

        return action switch
        {
            "list" => JsonSerializer.Serialize(new { autoloads = ReadAutoloads(projectFile) }, JsonOptions),
            "add" => AddAutoload(projectFile, ReadStringAny(args, true, "name")!, ReadStringAny(args, true, "path")!),
            "remove" => RemoveAutoload(projectFile, ReadStringAny(args, true, "name")!),
            _ => throw new InvalidOperationException("manage_autoloads action must be list/add/remove")
        };
    }

    private string ManageInputMap(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "list").ToLowerInvariant();
        var file = Path.Combine(projectPath, "project.godot");

        if (action == "list")
        {
            var settings = ReadGodotSettingsFile(file);
            var input = settings
                .Where(kv => kv.Key.StartsWith("input/", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key["input/".Length..], kv => kv.Value, StringComparer.Ordinal);
            return JsonSerializer.Serialize(new { input }, JsonOptions);
        }

        if (action == "add")
        {
            var actionName = ReadStringAny(args, true, "actionName", "name")!;
            var deadzone = ReadDoubleAny(args, 0.5, "deadzone");
            var value = "{\"deadzone\": " + deadzone.ToString(System.Globalization.CultureInfo.InvariantCulture) + ", \"events\": []}";
            UpsertProjectSetting(file, "input/" + actionName, value);
            return JsonSerializer.Serialize(new { success = true, action = "add", actionName }, JsonOptions);
        }

        if (action == "remove")
        {
            var actionName = ReadStringAny(args, true, "actionName", "name")!;
            RemoveSetting(file, "input", actionName);
            return JsonSerializer.Serialize(new { success = true, action = "remove", actionName }, JsonOptions);
        }

        throw new InvalidOperationException("manage_input_map action must be list/add/remove");
    }

    private string ManageExportPresets(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "list").ToLowerInvariant();
        var file = Path.Combine(projectPath, "export_presets.cfg");

        if (action == "list")
        {
            if (!File.Exists(file))
            {
                return JsonSerializer.Serialize(new { presets = Array.Empty<object>() }, JsonOptions);
            }

            var sections = File.ReadAllLines(file)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("[preset.", StringComparison.Ordinal) && line.EndsWith(']'))
                .ToArray();
            return JsonSerializer.Serialize(new { count = sections.Length, sections }, JsonOptions);
        }

        if (action == "add")
        {
            var presetName = ReadStringAny(args, true, "presetName", "name")!;
            var platform = ReadStringAny(args, false, "platform") ?? "Linux/X11";
            var exportPath = ReadStringAny(args, false, "exportPath") ?? "build/game.x86_64";

            var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
            var nextIndex = Regex.Matches(string.Join("\n", lines), @"\[preset\.(\d+)\]")
                .Select(m => int.Parse(m.Groups[1].Value))
                .DefaultIfEmpty(-1)
                .Max() + 1;

            lines.Add($"[preset.{nextIndex}]");
            lines.Add($"name=\"{presetName}\"");
            lines.Add($"platform=\"{platform}\"");
            lines.Add("runnable=true");
            lines.Add("dedicated_server=false");
            lines.Add("custom_features=\"\"");
            lines.Add("export_filter=\"all_resources\"");
            lines.Add("include_filter=\"\"");
            lines.Add("exclude_filter=\"\"");
            lines.Add($"export_path=\"{exportPath}\"");
            lines.Add("script_export_mode=1");
            lines.Add("script_encryption_key=\"\"");
            lines.Add(string.Empty);
            lines.Add($"[preset.{nextIndex}.options]");
            lines.Add("custom_template/debug=\"\"");
            lines.Add("custom_template/release=\"\"");
            lines.Add("binary_format/embed_pck=false");
            lines.Add("texture_format/s3tc_bptc=true");
            lines.Add("texture_format/etc2_astc=false");
            lines.Add("texture_format/etc2_astc_hdr=false");
            lines.Add(string.Empty);
            File.WriteAllLines(file, lines, new UTF8Encoding(false));

            return JsonSerializer.Serialize(new { success = true, action = "add", presetName, platform, exportPath }, JsonOptions);
        }

        throw new InvalidOperationException("manage_export_presets currently supports list/add");
    }

    private string ManageLayers(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "list").ToLowerInvariant();
        var file = Path.Combine(projectPath, "project.godot");

        if (action == "list")
        {
            var settings = ReadGodotSettingsFile(file);
            var layers = settings
                .Where(kv => kv.Key.StartsWith("layer_names/", StringComparison.Ordinal))
                .ToDictionary(kv => kv.Key["layer_names/".Length..], kv => TrimQuoted(kv.Value), StringComparer.Ordinal);
            return JsonSerializer.Serialize(new { layers }, JsonOptions);
        }

        if (action == "set")
        {
            var layerType = ReadStringAny(args, true, "layerType")!;
            var layer = ReadStringAny(args, true, "layer")!;
            var name = ReadStringAny(args, true, "name")!;
            UpsertProjectSetting(file, $"layer_names/{layerType}/layer_{layer}", '"' + name + '"');
            return JsonSerializer.Serialize(new { success = true, action = "set", layerType, layer, name }, JsonOptions);
        }

        throw new InvalidOperationException("manage_layers action must be list/set");
    }

    private string ManagePlugins(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "list").ToLowerInvariant();
        var file = Path.Combine(projectPath, "project.godot");

        if (action == "list")
        {
            var settings = ReadGodotSettingsFile(file);
            var enabled = settings
                .Where(kv => kv.Key.StartsWith("editor_plugins/", StringComparison.Ordinal) && kv.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Key["editor_plugins/".Length..].Replace("/enabled", string.Empty, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var addons = Path.Combine(projectPath, "addons");
            var available = Directory.Exists(addons)
                ? Directory.EnumerateDirectories(addons).Select(Path.GetFileName).OrderBy(x => x).ToArray()
                : Array.Empty<string>();

            return JsonSerializer.Serialize(new { enabled, available }, JsonOptions);
        }

        var pluginName = ReadStringAny(args, true, "pluginName", "name")!;
        if (action == "enable" || action == "disable")
        {
            UpsertProjectSetting(file, $"editor_plugins/{pluginName}/enabled", action == "enable" ? "true" : "false");
            return JsonSerializer.Serialize(new { success = true, action, pluginName }, JsonOptions);
        }

        throw new InvalidOperationException("manage_plugins action must be list/enable/disable");
    }

    private string ManageShader(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "read").ToLowerInvariant();
        var shaderPath = ReadStringAny(args, true, "shaderPath", "path")!;
        var full = ResolvePathInProject(projectPath, shaderPath);

        if (action == "read")
        {
            if (!File.Exists(full))
            {
                throw new FileNotFoundException("Shader not found", full);
            }

            return JsonSerializer.Serialize(new { shaderPath, source = File.ReadAllText(full) }, JsonOptions);
        }

        if (action == "create")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var shaderType = ReadStringAny(args, false, "shaderType") ?? "spatial";
            var source = ReadStringAny(args, false, "source")
                ?? $"shader_type {shaderType};\n\nvoid fragment() {{\n\tALBEDO = vec3(1.0);\n}}\n";
            File.WriteAllText(full, source, new UTF8Encoding(false));
            return JsonSerializer.Serialize(new { success = true, shaderPath }, JsonOptions);
        }

        throw new InvalidOperationException("manage_shader action must be read/create");
    }

    private string ManageTranslations(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "list").ToLowerInvariant();
        var file = Path.Combine(projectPath, "project.godot");

        if (action == "list")
        {
            var settings = ReadGodotSettingsFile(file);
            var value = settings.TryGetValue("internationalization/locale/translations", out var t) ? t : "PackedStringArray()";
            return JsonSerializer.Serialize(new { translations = value }, JsonOptions);
        }

        var translationPath = ReadStringAny(args, true, "translationPath", "path")!;
        var current = ReadGodotSettingsFile(file);
        var existing = current.TryGetValue("internationalization/locale/translations", out var raw)
            ? ParsePackedStringArray(raw)
            : new List<string>();

        if (action == "add")
        {
            if (!existing.Contains(translationPath, StringComparer.Ordinal))
            {
                existing.Add(translationPath);
            }
        }
        else if (action == "remove")
        {
            existing = existing.Where(x => !x.Equals(translationPath, StringComparison.Ordinal)).ToList();
        }
        else
        {
            throw new InvalidOperationException("manage_translations action must be list/add/remove");
        }

        var packed = "PackedStringArray(" + string.Join(", ", existing.Select(x => '"' + x + '"')) + ")";
        UpsertProjectSetting(file, "internationalization/locale/translations", packed);
        return JsonSerializer.Serialize(new { success = true, action, translations = existing }, JsonOptions);
    }

    private string CreateScript(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var scriptPath = ReadStringAny(args, true, "scriptPath", "path")!;
        var full = ResolvePathInProject(projectPath, scriptPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var language = (ReadStringAny(args, false, "language") ?? string.Empty).ToLowerInvariant();
        var extension = Path.GetExtension(full).ToLowerInvariant();
        var className = ReadStringAny(args, false, "className") ?? SanitizeIdentifier(Path.GetFileNameWithoutExtension(full));
        var baseType = ReadStringAny(args, false, "extends", "baseClass") ?? "Node";
        var namespaceName = ReadStringAny(args, false, "namespace") ?? "Game";
        var source = ReadStringAny(args, false, "source");

        if (string.IsNullOrWhiteSpace(source))
        {
            var csharpRequested = language == "csharp" || language == "cs" || extension == ".cs" || (!extension.Equals(".gd", StringComparison.Ordinal) && language != "gdscript");
            source = csharpRequested
                ? BuildCSharpScriptSource(namespaceName, className, baseType)
                : BuildGdScriptSource(className, baseType);
        }

        File.WriteAllText(full, source, new UTF8Encoding(false));

        return JsonSerializer.Serialize(new
        {
            success = true,
            scriptPath = Path.GetRelativePath(projectPath, full).Replace('\\', '/'),
            fullPath = full,
            language = extension == ".cs" ? "csharp" : (extension == ".gd" ? "gdscript" : language)
        }, JsonOptions);
    }

    private async Task<string> ExportProject(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        EnsureGodotProject(projectPath);
        EnsureMonoProjectArtifacts(projectPath);

        var presetName = ReadStringAny(args, true, "presetName")!;
        var outputPath = ReadStringAny(args, true, "outputPath")!;
        var resolvedOutputPath = Path.IsPathRooted(outputPath)
            ? outputPath
            : Path.GetFullPath(Path.Combine(projectPath, outputPath));
        var outputDir = Path.GetDirectoryName(resolvedOutputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        EnsureExportPresetExists(projectPath, presetName, resolvedOutputPath);
        var debug = ReadBoolAny(args, false, "debug");
        var godotPath = await DetectGodotPath(args);
        EnsureExportTemplatesForLinux(godotPath);

        var exportArgs = new List<string>
        {
            "--headless",
            "--path", projectPath,
            debug ? "--export-debug" : "--export-release",
            presetName,
            resolvedOutputPath
        };

        var (code, stdout, stderr) = await RunProcess(godotPath, exportArgs);
        return JsonSerializer.Serialize(new
        {
            success = code == 0,
            exitCode = code,
            presetName,
            outputPath = resolvedOutputPath,
            stdout,
            stderr
        }, JsonOptions);
    }

    private static void EnsureMonoProjectArtifacts(string projectPath)
    {
        var hasCSharpScripts = Directory
            .EnumerateFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Any(path => !path.Contains($"{Path.DirectorySeparatorChar}.godot{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        if (!hasCSharpScripts)
        {
            return;
        }

        var projectFile = Path.Combine(projectPath, "project.godot");
        var settings = ReadGodotSettingsFile(projectFile);
        var projectName = TrimQuoted(settings.TryGetValue("dotnet/project/assembly_name", out var assemblyNameRaw)
            ? assemblyNameRaw
            : Path.GetFileName(projectPath));
        projectName = SanitizeIdentifier(projectName);

        var csprojFiles = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.TopDirectoryOnly).ToArray();
        var csprojPath = Path.Combine(projectPath, projectName + ".csproj");
        if (!File.Exists(csprojPath))
        {
            if (csprojFiles.Length == 0)
            {
                var namespaceName = TrimQuoted(settings.TryGetValue("application/config/name", out var appNameRaw)
                    ? appNameRaw
                    : projectName);
                CreateMonoCsprojFile(csprojPath, namespaceName);
                csprojFiles = [csprojPath];
            }
            else
            {
                csprojPath = csprojFiles[0];
            }
        }

        var expectedSlnPath = Path.Combine(projectPath, projectName + ".sln");
        if (!File.Exists(expectedSlnPath))
        {
            CreateMonoSlnFile(expectedSlnPath, Path.GetFileNameWithoutExtension(csprojPath), Path.GetFileName(csprojPath));
        }
    }

    private static void EnsureExportPresetExists(string projectPath, string presetName, string outputPath)
    {
        var file = Path.Combine(projectPath, "export_presets.cfg");
        if (File.Exists(file))
        {
            var current = File.ReadAllText(file);
            if (current.Contains($"name=\"{presetName}\"", StringComparison.Ordinal))
            {
                return;
            }
        }

        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        var nextIndex = Regex.Matches(string.Join("\n", lines), @"\[preset\.(\d+)\]")
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(-1)
            .Max() + 1;

        var relativeOutput = Path.GetRelativePath(projectPath, outputPath).Replace('\\', '/');
        if (relativeOutput.StartsWith("..", StringComparison.Ordinal))
        {
            relativeOutput = "build/game.x86_64";
        }

        var platform = presetName.Contains('/', StringComparison.Ordinal) ? presetName : "Linux/X11";

        lines.Add($"[preset.{nextIndex}]");
        lines.Add($"name=\"{presetName}\"");
        lines.Add($"platform=\"{platform}\"");
        lines.Add("runnable=true");
        lines.Add("dedicated_server=false");
        lines.Add("custom_features=\"\"");
        lines.Add("export_filter=\"all_resources\"");
        lines.Add("include_filter=\"\"");
        lines.Add("exclude_filter=\"\"");
        lines.Add($"export_path=\"{relativeOutput}\"");
        lines.Add("script_export_mode=1");
        lines.Add("script_encryption_key=\"\"");
        lines.Add(string.Empty);
        lines.Add($"[preset.{nextIndex}.options]");
        lines.Add("custom_template/debug=\"\"");
        lines.Add("custom_template/release=\"\"");
        lines.Add("binary_format/embed_pck=false");
        lines.Add("texture_format/s3tc_bptc=true");
        lines.Add("texture_format/etc2_astc=false");
        lines.Add("texture_format/etc2_astc_hdr=false");
        lines.Add(string.Empty);
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    private static void CreateMonoCsprojFile(string csprojPath, string namespaceName)
    {
        var safeNamespace = SanitizeIdentifier(namespaceName);
        var csprojText =
            "<Project Sdk=\"Godot.NET.Sdk/4.6.2\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            $"    <RootNamespace>{safeNamespace}</RootNamespace>\n" +
            "    <Nullable>enable</Nullable>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n";

        File.WriteAllText(csprojPath, csprojText, new UTF8Encoding(false));
    }

    private static void CreateMonoSlnFile(string slnPath, string projectName, string csprojFileName)
    {
        var projectGuid = Guid.NewGuid().ToString().ToUpperInvariant();
        var slnText =
            "Microsoft Visual Studio Solution File, Format Version 12.00\n" +
            "# Visual Studio Version 17\n" +
            "VisualStudioVersion = 17.0.31903.59\n" +
            "MinimumVisualStudioVersion = 10.0.40219.1\n" +
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{projectName}\", \"{csprojFileName}\", \"{{{projectGuid}}}\"\n" +
            "EndProject\n" +
            "Global\n" +
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\n" +
            "\t\tDebug|Any CPU = Debug|Any CPU\n" +
            "\t\tRelease|Any CPU = Release|Any CPU\n" +
            "\tEndGlobalSection\n" +
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\n" +
            "\tEndGlobalSection\n" +
            "EndGlobal\n";

        File.WriteAllText(slnPath, slnText, new UTF8Encoding(false));
    }

    private static void EnsureExportTemplatesForLinux(string godotPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return;
        }

        var templateDir = Path.Combine(home, ".local", "share", "godot", "export_templates", "4.6.2.stable.mono");
        Directory.CreateDirectory(templateDir);

        var debugTemplate = Path.Combine(templateDir, "linux_debug.x86_64");
        var releaseTemplate = Path.Combine(templateDir, "linux_release.x86_64");

        if (!File.Exists(debugTemplate))
        {
            File.Copy(godotPath, debugTemplate, overwrite: true);
        }

        if (!File.Exists(releaseTemplate))
        {
            File.Copy(godotPath, releaseTemplate, overwrite: true);
        }
    }

    private string ManageCiPipeline(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "create").ToLowerInvariant();
        var workflowPath = Path.Combine(projectPath, ".github", "workflows", "godot-mono-export.yml");

        if (action == "read")
        {
            return JsonSerializer.Serialize(new
            {
                exists = File.Exists(workflowPath),
                workflowPath,
                content = File.Exists(workflowPath) ? File.ReadAllText(workflowPath) : string.Empty
            }, JsonOptions);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(workflowPath)!);
        var text =
"""
name: Godot Mono Export

on:
  push:
    branches: [ main ]
  workflow_dispatch:

jobs:
  export:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - name: Build C# project
        run: dotnet build
      - name: Export (placeholder)
        run: echo "Use godot --headless --export-release in your environment"
""";
        File.WriteAllText(workflowPath, text, new UTF8Encoding(false));

        return JsonSerializer.Serialize(new { success = true, workflowPath }, JsonOptions);
    }

    private string ManageDockerExport(Dictionary<string, JsonElement> args)
    {
        var projectPath = RequireProjectPath(args);
        var action = (ReadStringAny(args, false, "action") ?? "create").ToLowerInvariant();
        var dockerfile = Path.Combine(projectPath, "Dockerfile.godot-mono-export");

        if (action == "read")
        {
            return JsonSerializer.Serialize(new
            {
                exists = File.Exists(dockerfile),
                dockerfile,
                content = File.Exists(dockerfile) ? File.ReadAllText(dockerfile) : string.Empty
            }, JsonOptions);
        }

        var baseImage = ReadStringAny(args, false, "baseImage") ?? "ubuntu:22.04";
        var text =
            $"FROM {baseImage}\n" +
            "RUN apt-get update && apt-get install -y wget unzip ca-certificates dotnet-sdk-8.0\n" +
            "WORKDIR /app\n" +
            "COPY . .\n" +
            "RUN dotnet build\n" +
            "CMD [\"bash\", \"-lc\", \"echo install Godot Mono 4.6.2 and run export command here\"]\n";
        File.WriteAllText(dockerfile, text, new UTF8Encoding(false));

        return JsonSerializer.Serialize(new { success = true, dockerfile, baseImage }, JsonOptions);
    }

    private async Task<string> DetectGodotPath(Dictionary<string, JsonElement> args)
    {
        var explicitPath = ReadStringAny(args, false, "godotPath")
            ?? Environment.GetEnvironmentVariable("GODOT_MONO_PATH")
            ?? Environment.GetEnvironmentVariable("GODOT_PATH");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        candidates.AddRange([
            "godot-mono",
            "godot4-mono",
            "godot",
            "Godot_v4.6.2-stable_mono_linux.x86_64"
        ]);

        if (OperatingSystem.IsWindows())
        {
            candidates.AddRange([
                @"C:\Program Files\Godot\Godot_v4.6.2-stable_mono_win64.exe",
                @"C:\Program Files\Godot\Godot_v4.6-stable_mono_win64.exe"
            ]);
        }

        if (OperatingSystem.IsMacOS())
        {
            candidates.AddRange([
                "/Applications/Godot_mono.app/Contents/MacOS/Godot",
                "/Applications/Godot.app/Contents/MacOS/Godot"
            ]);
        }

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var (code, stdout, stderr) = await RunProcess(candidate, ["--version"]);
                if (code != 0)
                {
                    continue;
                }

                var versionText = (stdout + "\n" + stderr).ToLowerInvariant();
                if (!versionText.Contains("4.", StringComparison.Ordinal))
                {
                    continue;
                }

                return candidate;
            }
            catch
            {
                // continue
            }
        }

        throw new InvalidOperationException("Could not detect Godot executable. Set GODOT_MONO_PATH to your Godot Mono 4.6.2 binary.");
    }

    private async Task<bool> ConnectRuntimeAsync(TimeSpan timeout)
    {
        DisposeRuntimeConnection();

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", _runtimePort);

                _runtimeClient = client;
                var stream = client.GetStream();
                _runtimeReader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
                _runtimeWriter = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                return true;
            }
            catch
            {
                await Task.Delay(500);
            }
        }

        return false;
    }

    private void EnsureRuntimeScriptInstalled(string projectPath)
    {
        var source = GetAssetPath("mcp_interaction_server.gd");
        var targetDir = Path.Combine(projectPath, "addons", "godot_mono_mcp");
        Directory.CreateDirectory(targetDir);

        var targetScript = Path.Combine(targetDir, "mcp_interaction_server.gd");
        File.Copy(source, targetScript, overwrite: true);

        AddOrUpdateAutoload(Path.Combine(projectPath, "project.godot"), "McpInteractionServer", "*res://addons/godot_mono_mcp/mcp_interaction_server.gd");
    }

    private static string RuntimeCommandForTool(string toolName)
    {
        return toolName switch
        {
            "game_get_ui" => "get_ui_elements",
            "game_get_scene_tree" => "get_scene_tree",
            "game_performance" => "get_performance",
            "game_os_info" => "os_info",
            _ => toolName[5..]
        };
    }

    private static string HeadlessOperationForTool(string toolName)
    {
        return toolName switch
        {
            "modify_scene_node" => "modify_node",
            "remove_scene_node" => "remove_node",
            "update_project_uids" => "resave_resources",
            _ => toolName
        };
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunProcess(string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process: {fileName}");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string GetAssetPath(string fileName)
    {
        var candidates = new List<string>
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "godot", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assets", "godot", fileName)),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "assets", "godot", fileName)),
            Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "assets", "godot", fileName))
        };

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            throw new FileNotFoundException($"Missing asset file: {fileName}", string.Join(" | ", candidates));
        }

        return path;
    }

    private static int GetAvailableLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private void CaptureLog(List<string> target, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_logLock)
        {
            target.Add(line);
            if (target.Count > 8000)
            {
                target.RemoveRange(0, target.Count - 8000);
            }
        }
    }

    private static Dictionary<string, object?> ConvertArgumentsToPlainObject(Dictionary<string, JsonElement> args, bool snakeCaseKeys = false)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in args)
        {
            result[snakeCaseKeys ? ToSnakeCase(key) : key] = JsonToObject(value, snakeCaseKeys);
        }

        return result;
    }

    private static object? JsonToObject(JsonElement element, bool snakeCaseKeys)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(
                p => snakeCaseKeys ? ToSnakeCase(p.Name) : p.Name,
                p => JsonToObject(p.Value, snakeCaseKeys),
                StringComparer.Ordinal),
            JsonValueKind.Array => element.EnumerateArray().Select(x => JsonToObject(x, snakeCaseKeys)).ToArray(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt64(out var intValue) => intValue,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsUpper(ch))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    private static string RequireProjectPath(Dictionary<string, JsonElement> args)
    {
        var value = ReadStringAny(args, false, "projectPath")
            ?? Environment.GetEnvironmentVariable("GODOT_PROJECT_PATH")
            ?? Directory.GetCurrentDirectory();

        var path = Path.GetFullPath(value);
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"Project path not found: {path}");
        }

        return path;
    }

    private static void EnsureGodotProject(string projectPath)
    {
        if (!File.Exists(Path.Combine(projectPath, "project.godot")))
        {
            throw new InvalidOperationException($"Not a valid Godot project root: {projectPath}");
        }
    }

    private static string ResolvePathInProject(string projectPath, string path)
    {
        var root = Path.GetFullPath(projectPath);
        var full = path.StartsWith("res://", StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(root, path[6..].Replace('/', Path.DirectorySeparatorChar)))
            : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path escapes project root");
        }

        return full;
    }

    private static string? ReadStringAny(Dictionary<string, JsonElement> args, bool required, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!args.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        if (required)
        {
            throw new InvalidOperationException("Missing required argument: " + string.Join("/", keys));
        }

        return null;
    }

    private static bool ReadBoolAny(Dictionary<string, JsonElement> args, bool defaultValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!args.TryGetValue(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        }

        return defaultValue;
    }

    private static double ReadDoubleAny(Dictionary<string, JsonElement> args, double defaultValue, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!args.TryGetValue(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d))
            {
                return d;
            }

            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static Dictionary<string, string> ReadGodotSettingsFile(string filePath)
    {
        var lines = File.Exists(filePath) ? File.ReadAllLines(filePath) : Array.Empty<string>();
        var section = string.Empty;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            var index = line.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = line[..index].Trim();
            var value = line[(index + 1)..].Trim();
            var fullKey = string.IsNullOrEmpty(section) ? key : section + "/" + key;
            result[fullKey] = value;
        }

        return result;
    }

    private static void UpsertProjectSetting(string projectFile, string fullKey, string value)
    {
        var lines = File.Exists(projectFile) ? File.ReadAllLines(projectFile).ToList() : [];

        var slash = fullKey.LastIndexOf('/');
        var section = slash > 0 ? fullKey[..slash] : "application";
        var key = slash > 0 ? fullKey[(slash + 1)..] : fullKey;

        var sectionHeader = "[" + section + "]";
        var sectionStart = lines.FindIndex(line => line.Trim().Equals(sectionHeader, StringComparison.Ordinal));

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add(sectionHeader);
            lines.Add(key + "=" + value);
            File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
            return;
        }

        var sectionEnd = lines.Count;
        for (var i = sectionStart + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                sectionEnd = i;
                break;
            }
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (lines[i].TrimStart().StartsWith(key + "=", StringComparison.Ordinal))
            {
                lines[i] = key + "=" + value;
                File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
                return;
            }
        }

        lines.Insert(sectionEnd, key + "=" + value);
        File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
    }

    private static void RemoveSetting(string projectFile, string section, string key)
    {
        if (!File.Exists(projectFile))
        {
            return;
        }

        var lines = File.ReadAllLines(projectFile).ToList();
        var sectionHeader = "[" + section + "]";
        var sectionStart = lines.FindIndex(line => line.Trim().Equals(sectionHeader, StringComparison.Ordinal));
        if (sectionStart < 0)
        {
            return;
        }

        var sectionEnd = lines.Count;
        for (var i = sectionStart + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                sectionEnd = i;
                break;
            }
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (lines[i].TrimStart().StartsWith(key + "=", StringComparison.Ordinal))
            {
                lines.RemoveAt(i);
                break;
            }
        }

        File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
    }

    private static Dictionary<string, string> ReadAutoloads(string projectFile)
    {
        var settings = ReadGodotSettingsFile(projectFile);
        return settings
            .Where(kv => kv.Key.StartsWith("autoload/", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key["autoload/".Length..], kv => TrimQuoted(kv.Value), StringComparer.Ordinal);
    }

    private static string AddAutoload(string projectFile, string name, string path)
    {
        var value = path.StartsWith("*", StringComparison.Ordinal) ? path : "*" + path;
        AddOrUpdateAutoload(projectFile, name, value);
        return JsonSerializer.Serialize(new { success = true, name, path = value }, JsonOptions);
    }

    private static string RemoveAutoload(string projectFile, string name)
    {
        var lines = File.Exists(projectFile) ? File.ReadAllLines(projectFile).ToList() : [];
        var keyPrefix = name + "=";

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].TrimStart().StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                lines.RemoveAt(i);
            }
        }

        File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
        return JsonSerializer.Serialize(new { success = true, name }, JsonOptions);
    }

    private static void AddOrUpdateAutoload(string projectFile, string name, string value)
    {
        var lines = File.Exists(projectFile) ? File.ReadAllLines(projectFile).ToList() : [];
        var sectionHeader = "[autoload]";
        var sectionStart = lines.FindIndex(line => line.Trim().Equals(sectionHeader, StringComparison.Ordinal));

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
            {
                lines.Add(string.Empty);
            }

            lines.Add(sectionHeader);
            lines.Add(name + "=\"" + value + "\"");
            File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
            return;
        }

        var sectionEnd = lines.Count;
        for (var i = sectionStart + 1; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                sectionEnd = i;
                break;
            }
        }

        var keyPrefix = name + "=";
        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (lines[i].TrimStart().StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                lines[i] = name + "=\"" + value + "\"";
                File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
                return;
            }
        }

        lines.Insert(sectionEnd, name + "=\"" + value + "\"");
        File.WriteAllLines(projectFile, lines, new UTF8Encoding(false));
    }

    private static List<string> ParsePackedStringArray(string value)
    {
        var result = new List<string>();
        var matches = Regex.Matches(value, "\"([^\"]+)\"");
        foreach (Match match in matches)
        {
            if (match.Success)
            {
                result.Add(match.Groups[1].Value);
            }
        }

        return result;
    }

    private static string BuildCSharpScriptSource(string namespaceName, string className, string baseType)
    {
        var safeNamespace = SanitizeIdentifier(namespaceName);
        var safeClass = SanitizeIdentifier(className);
        var safeBase = string.IsNullOrWhiteSpace(baseType) ? "Node" : baseType.Trim();

        return
            "using Godot;\n\n" +
            $"namespace {safeNamespace};\n\n" +
            $"public partial class {safeClass} : {safeBase}\n" +
            "{\n" +
            "    public override void _Ready()\n" +
            "    {\n" +
            "    }\n" +
            "}\n";
    }

    private static string BuildGdScriptSource(string className, string baseType)
    {
        var safeBase = string.IsNullOrWhiteSpace(baseType) ? "Node" : baseType.Trim();
        var safeClass = SanitizeIdentifier(className);

        return
            $"extends {safeBase}\n" +
            $"class_name {safeClass}\n\n" +
            "func _ready() -> void:\n" +
            "    pass\n";
    }

    private static string SanitizeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "GameScript";
        }

        var chars = value.Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray();
        var cleaned = new string(chars);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "GameScript";
        }

        if (char.IsDigit(cleaned[0]))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    private static string TrimQuoted(string value)
    {
        if (value.Length >= 2 && value.StartsWith('"') && value.EndsWith('"'))
        {
            return value[1..^1];
        }

        return value;
    }

    private void DisposeRuntimeConnection()
    {
        try { _runtimeWriter?.Dispose(); } catch { }
        try { _runtimeReader?.Dispose(); } catch { }
        try { _runtimeClient?.Dispose(); } catch { }

        _runtimeWriter = null;
        _runtimeReader = null;
        _runtimeClient = null;
    }

}

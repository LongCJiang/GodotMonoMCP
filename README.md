# GodotMonoMCP

Godot Mono 4.6.2 的 MCP 服务实现，面向 C#/Mono 工作流。

## 来源说明

本项目基于以下仓库的工具集设计与能力边界进行实现与适配：

- https://github.com/tugcantopaloglu/godot-mcp

本仓库目标是在 **Godot Mono 4.6.2** 下提供等价工具覆盖，并针对 Mono/C# 场景做可用性增强。

## 主要能力

- 覆盖参考仓库工具名集合（154 个）
- `game_*` 运行时工具：通过 `McpInteractionServer`（TCP）执行
- 场景/资源头less工具：通过 `godot_operations.gd` 执行
- 项目/文件/配置工具：C# 本地实现
- `export_project` 针对 Mono 导出做自动修复：
  - 自动补齐 `.csproj` / `.sln`（缺失时）
  - 自动补齐 `export_presets.cfg` preset（缺失时）

## 目录结构

- `src/GodotMonoMcp`：MCP 服务（.NET）
- `assets/godot`：Godot 侧脚本资产
- `scripts`：全工具冒烟脚本

## 环境要求

- .NET SDK 10+
- Godot Mono 4.6.2

建议设置：

- `GODOT_MONO_PATH`：Godot Mono 可执行文件路径
- `GODOT_PROJECT_PATH`：默认 Godot 项目路径

## 本地运行

```bash
cd src/GodotMonoMcp
dotnet run
```

## Claude Code MCP 配置示例

```bash
claude mcp add godot-mono --scope user \
  --env GODOT_MONO_PATH=/path/to/Godot\
  --env GODOT_PROJECT_PATH=/path/to/your-project \
  -- dotnet /abs/path/to/GodotMonoMcp.dll
```

建议先构建后使用 DLL 方式启动：

```bash
dotnet build /abs/path/to/GodotMonoMcp.csproj
```

## 冒烟测试

```bash
cd /path/to/GodotMonoMCP
./scripts/smoke_all_tools.sh --project /path/to/godot-project
```

输出：

- `smoke-results/summary.json`
- `smoke-results/results.jsonl`

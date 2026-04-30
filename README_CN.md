# GodotMonoMCP

[English](README.md) | 中文

Godot Mono 4.6.2 的 MCP 服务实现，面向 C#/Mono 工作流。

兼容 **MCP 2024-11-05** 与 **2025-03-26** 协议版本，支持 **stdio** 与 **SSE** 两种传输模式。

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
- `scripts`：辅助脚本

## 环境要求

- .NET SDK 10+
- Godot Mono 4.6.2

建议设置：

- `GODOT_MONO_PATH`：Godot Mono 可执行文件路径
- `GODOT_PROJECT_PATH`：默认 Godot 项目路径

## 本地运行

### stdio 模式（默认）

```bash
cd src/GodotMonoMcp
dotnet run
```

### SSE 模式

```bash
cd src/GodotMonoMcp
dotnet run -- --transport sse --port 3000
```

帮助信息：

```bash
dotnet run -- --help
```

## 各工具配置示例

### Claude Code

```bash
claude mcp add godot-mono --scope user \
  --env GODOT_MONO_PATH=/path/to/Godot \
  --env GODOT_PROJECT_PATH=/path/to/your-project \
  -- dotnet /abs/path/to/GodotMonoMcp.dll
```

建议先构建后使用 DLL 方式启动：

```bash
dotnet build /abs/path/to/GodotMonoMcp.csproj
```

### Kimi CLI (Kimi Code)

在 `~/.kimi/mcp.json` 中添加：

```json
{
  "mcpServers": {
    "godot-mono": {
      "command": "dotnet",
      "args": [
        "/abs/path/to/GodotMonoMcp.dll"
      ],
      "env": {
        "GODOT_MONO_PATH": "/path/to/Godot",
        "GODOT_PROJECT_PATH": "/path/to/your-project"
      }
    }
  }
}
```

### OpenCode

在 `.opencode/config.yaml` 或 `~/.opencode/config.yaml` 中添加：

```yaml
mcp_servers:
  godot-mono:
    command: dotnet
    args:
      - /abs/path/to/GodotMonoMcp.dll
    env:
      GODOT_MONO_PATH: /path/to/Godot
      GODOT_PROJECT_PATH: /path/to/your-project
```

### OpenAI Codex CLI

在 `.codex/config.yaml` 或 `~/.codex/config.yaml` 中添加：

```yaml
mcp_servers:
  godot-mono:
    command: dotnet
    args:
      - /abs/path/to/GodotMonoMcp.dll
    env:
      GODOT_MONO_PATH: /path/to/Godot
      GODOT_PROJECT_PATH: /path/to/your-project
```

### Cline / Roo Code

在 `cline_mcp_settings.json` 中添加：

```json
{
  "mcpServers": {
    "godot-mono": {
      "command": "dotnet",
      "args": ["/abs/path/to/GodotMonoMcp.dll"],
      "env": {
        "GODOT_MONO_PATH": "/path/to/Godot",
        "GODOT_PROJECT_PATH": "/path/to/your-project"
      },
      "disabled": false,
      "autoApprove": []
    }
  }
}
```

### Continue

在 `~/.continue/config.json` 中添加：

```json
{
  "server": {
    "mcpServers": [
      {
        "name": "godot-mono",
        "transport": {
          "type": "stdio",
          "command": "dotnet",
          "args": ["/abs/path/to/GodotMonoMcp.dll"],
          "env": {
            "GODOT_MONO_PATH": "/path/to/Godot",
            "GODOT_PROJECT_PATH": "/path/to/your-project"
          }
        }
      }
    ]
  }
}
```

### Cursor

在 Cursor 设置中找到 **MCP** 配置页，或通过菜单 `Cursor Settings → MCP` 打开，添加：

```json
{
  "mcpServers": {
    "godot-mono": {
      "command": "dotnet",
      "args": ["/abs/path/to/GodotMonoMcp.dll"],
      "env": {
        "GODOT_MONO_PATH": "/path/to/Godot",
        "GODOT_PROJECT_PATH": "/path/to/your-project"
      }
    }
  }
}
```

或手动编辑 `~/.cursor/mcp.json`（全局）或项目根目录的 `.cursor/mcp.json`。

### Windsurf

在 Windsurf 设置中找到 **Cascade** → **MCP Servers**，添加：

```json
{
  "mcpServers": {
    "godot-mono": {
      "command": "dotnet",
      "args": ["/abs/path/to/GodotMonoMcp.dll"],
      "env": {
        "GODOT_MONO_PATH": "/path/to/Godot",
        "GODOT_PROJECT_PATH": "/path/to/your-project"
      }
    }
  }
}
```

或手动编辑 `~/.windsurf/mcp.json`。

### VS Code + GitHub Copilot Chat

安装 **MCP Server** 扩展（如 `Microsoft Copilot Chat` 已内置 MCP 支持），在 VS Code 用户设置 (`settings.json`) 中添加：

```json
{
  "mcp": {
    "servers": {
      "godot-mono": {
        "command": "dotnet",
        "args": ["/abs/path/to/GodotMonoMcp.dll"],
        "env": {
          "GODOT_MONO_PATH": "/path/to/Godot",
          "GODOT_PROJECT_PATH": "/path/to/your-project"
        }
      }
    }
  }
}
```

或使用 VS Code 的 **MCP: Add Server** 命令进行图形化配置。

### SSE 模式通用配置

如果客户端支持 SSE 传输，可以先启动 SSE 服务器：

```bash
dotnet /abs/path/to/GodotMonoMcp.dll --transport sse --port 3000
```

然后在客户端配置中指定 SSE URL：

```json
{
  "mcpServers": {
    "godot-mono": {
      "url": "http://127.0.0.1:3000/sse"
    }
  }
}
```


## 完整工具列表

本项目共暴露 **154** 个 MCP 工具，按功能域划分如下：

### 项目管理

`launch_editor`, `run_project`, `stop_project`, `create_project`, `export_project`, `get_godot_version`, `get_project_info`, `list_projects`, `get_uid`, `update_project_uids`, `list_project_files`

### 文件与资源

`read_file`, `write_file`, `delete_file`, `rename_file`, `create_directory`, `load_sprite`, `create_resource`, `manage_resource`, `game_resource`, `attach_script`, `create_script`, `manage_shader`, `manage_theme_resource`, `export_mesh_library`, `game_script`

### 场景与节点

`create_scene`, `read_scene`, `save_scene`, `set_main_scene`, `modify_scene_node`, `remove_scene_node`, `manage_scene_structure`, `manage_scene_signals`, `add_node`, `game_instantiate_scene`, `game_remove_node`, `game_get_nodes_in_group`, `game_find_nodes_by_class`, `game_reparent_node`, `game_spawn_node`

### 运行时调试

`get_debug_output`, `game_screenshot`, `game_get_scene_tree`, `game_eval`, `game_get_node_info`, `game_performance`, `game_wait`, `game_get_errors`, `game_get_logs`, `game_debug_draw`

### 属性与信号

`game_get_property`, `game_set_property`, `game_call_method`, `game_connect_signal`, `game_disconnect_signal`, `game_emit_signal`, `game_list_signals`, `game_await_signal`

### 输入交互

`game_click`, `game_key_press`, `game_key_hold`, `game_key_release`, `game_mouse_move`, `game_mouse_drag`, `game_scroll`, `game_gamepad`, `game_touch`, `game_input_state`, `game_input_action`

### 动画与骨骼

`game_play_animation`, `game_create_animation`, `game_tween_property`, `game_animation_tree`, `game_animation_control`, `game_bone_pose`, `game_skeleton_ik`

### 物理与碰撞

`game_add_collision`, `game_physics_body`, `game_create_joint`, `game_physics_2d`, `game_physics_3d`

### 2D系统

`game_light_2d`, `game_shape_2d`, `game_path_2d`, `game_parallax`, `game_canvas`, `game_canvas_draw`

### 3D系统

`game_csg`, `game_light_3d`, `game_mesh_instance`, `game_gridmap`, `game_path_3d`, `game_sky`, `game_camera_attributes`, `game_navigation_3d`, `game_terrain`

### 渲染与特效

`game_environment`, `game_viewport`, `game_3d_effects`, `game_gi`, `game_render_settings`, `game_visual_shader`

### 音频系统

`game_get_audio`, `game_audio_play`, `game_audio_bus`, `game_audio_effect`, `game_audio_bus_layout`, `game_audio_spatial`

### UI系统

`game_get_ui`, `game_ui_theme`, `game_ui_control`, `game_ui_text`, `game_ui_popup`, `game_ui_tree`, `game_ui_item_list`, `game_ui_tabs`, `game_ui_menu`, `game_ui_range`

### 网络与多人

`game_http_request`, `game_websocket`, `game_multiplayer`, `game_rpc`

### 项目配置

`read_project_settings`, `modify_project_settings`, `manage_autoloads`, `manage_input_map`, `manage_export_presets`, `manage_layers`, `manage_plugins`, `manage_translations`, `game_window`, `game_os_info`, `game_time_scale`, `game_process_mode`, `game_world_settings`, `game_locale`, `manage_ci_pipeline`, `manage_docker_export`

### 其他运行时

`game_change_scene`, `game_pause`, `game_get_camera`, `game_set_camera`, `game_raycast`, `game_navigate_path`, `game_tilemap`, `game_set_shader_param`, `game_set_particles`, `game_create_timer`, `game_manage_group`, `game_serialize_state`, `game_multimesh`, `game_procedural_mesh`, `game_video`

> 完整工具名集合定义于 [`src/GodotMonoMcp/ToolCatalog.cs`](src/GodotMonoMcp/ToolCatalog.cs)。

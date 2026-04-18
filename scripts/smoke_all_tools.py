#!/usr/bin/env python3
import argparse
import json
import os
import sys
import time
import subprocess
from pathlib import Path
from typing import Any, Dict, List, Tuple


class McpClient:
    def __init__(self, csproj_path: Path, timeout_sec: float = 60.0) -> None:
        self.csproj_path = csproj_path
        self.timeout_sec = timeout_sec
        self.proc: subprocess.Popen[str] | None = None
        self._id = 1

    def start(self) -> None:
        self.proc = subprocess.Popen(
            ["dotnet", "run", "--project", str(self.csproj_path)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
        )

    def stop(self) -> None:
        if self.proc is None:
            return
        try:
            if self.proc.stdin:
                self.proc.stdin.close()
        except Exception:
            pass
        try:
            self.proc.terminate()
            self.proc.wait(timeout=3)
        except Exception:
            try:
                self.proc.kill()
            except Exception:
                pass

    def call(self, method: str, params: Dict[str, Any] | None = None, timeout_sec: float | None = None) -> Dict[str, Any]:
        if self.proc is None or self.proc.stdin is None or self.proc.stdout is None:
            raise RuntimeError("MCP process not started")

        req_id = self._id
        self._id += 1

        payload: Dict[str, Any] = {
            "jsonrpc": "2.0",
            "id": req_id,
            "method": method,
        }
        if params is not None:
            payload["params"] = params

        self.proc.stdin.write(json.dumps(payload, ensure_ascii=False) + "\n")
        self.proc.stdin.flush()

        deadline = time.time() + (timeout_sec or self.timeout_sec)
        while time.time() < deadline:
            line = self.proc.stdout.readline()
            if not line:
                if self.proc.poll() is not None:
                    stderr = ""
                    if self.proc.stderr:
                        stderr = self.proc.stderr.read()
                    raise RuntimeError(f"MCP process exited early: {stderr}")
                time.sleep(0.05)
                continue

            try:
                msg = json.loads(line)
            except json.JSONDecodeError:
                continue

            if msg.get("id") == req_id:
                return msg

        raise TimeoutError(f"Timeout waiting response for method={method}")


def ensure_test_project(project_path: Path) -> None:
    project_path.mkdir(parents=True, exist_ok=True)
    export_cfg = project_path / "export_presets.cfg"
    if export_cfg.exists():
        export_cfg.unlink()

    project_godot = project_path / "project.godot"
    if not project_godot.exists():
        project_godot.write_text(
            """
config_version=5

[application]
config/name="SmokeProject"
config/features=PackedStringArray("4.6", "C#", "Mono")
run/main_scene="res://scenes/Main.tscn"

[dotnet]
project/assembly_name="SmokeProject"
""".strip()
            + "\n",
            encoding="utf-8",
        )

    (project_path / "scenes").mkdir(exist_ok=True)
    smoke_main_tscn = project_path / "scenes" / "SmokeMain.tscn"
    smoke_main_tscn.write_text(
        """
[gd_scene format=3]

[node name="SmokeMain" type="Node"]

[node name="Camera3D" type="Camera3D" parent="."]
current = true

[node name="AnimationPlayer" type="AnimationPlayer" parent="."]
[node name="AnimationTree" type="AnimationTree" parent="."]
[node name="Audio3D" type="AudioStreamPlayer3D" parent="."]
[node name="GridMap" type="GridMap" parent="."]
[node name="Skeleton3D" type="Skeleton3D" parent="."]
[node name="SkeletonIK3D" type="SkeletonIK3D" parent="."]
[node name="TileMapLayer" type="TileMapLayer" parent="."]
[node name="Area2D" type="Area2D" parent="."]
[node name="Area3D" type="Area3D" parent="."]
[node name="MeshInstance3D" type="MeshInstance3D" parent="."]
[node name="CSGBox3D" type="CSGBox3D" parent="."]
[node name="NavigationRegion3D" type="NavigationRegion3D" parent="."]
[node name="Path2D" type="Path2D" parent="."]
[node name="Path3D" type="Path3D" parent="."]
[node name="RigidBody3D" type="RigidBody3D" parent="."]
[node name="Particles3D" type="GPUParticles3D" parent="."]
[node name="Line2D" type="Line2D" parent="."]
[node name="ToRemove" type="Node" parent="."]
[node name="ToReparent" type="Node" parent="."]

[node name="UIRoot" type="Control" parent="."]
[node name="Tree" type="Tree" parent="UIRoot"]
[node name="PopupMenu" type="PopupMenu" parent="UIRoot"]
[node name="PopupPanel" type="PopupPanel" parent="UIRoot"]
[node name="ItemList" type="ItemList" parent="UIRoot"]
[node name="TabContainer" type="TabContainer" parent="UIRoot"]
[node name="LineEdit" type="LineEdit" parent="UIRoot"]
[node name="TextEdit" type="TextEdit" parent="UIRoot"]
[node name="ProgressBar" type="ProgressBar" parent="UIRoot"]
[node name="Slider" type="HSlider" parent="UIRoot"]
""".strip()
        + "\n",
        encoding="utf-8",
    )

    (project_path / "Scripts").mkdir(exist_ok=True)
    player_cs = project_path / "Scripts" / "Player.cs"
    if not player_cs.exists():
        player_cs.write_text(
            """
using Godot;

public partial class Player : Node2D
{
    public override void _Ready()
    {
    }
}
""".strip()
            + "\n",
            encoding="utf-8",
        )


def build_tool_args(name: str, project_path: Path) -> Tuple[Dict[str, Any], str | None]:
    rel_main_scene = "scenes/SmokeMain.tscn"
    rel_player = "Scripts/Player.cs"
    root_path = "/root/SmokeMain"

    default_args: Dict[str, Any] = {"projectPath": str(project_path)}

    if name == "create_project":
        return {
            "projectPath": str(project_path / "_smoke_created_project"),
            "projectName": "SmokeCreatedProject",
        }, None

    if name == "run_project":
        return {"projectPath": str(project_path), "scene": "res://scenes/SmokeMain.tscn"}, None

    if name in {"stop_project", "get_debug_output", "get_godot_version", "game_get_logs", "game_get_errors"}:
        return {}, None

    if name == "list_projects":
        return {"directory": str(project_path.parent), "recursive": False}, None

    if name == "get_project_info":
        return default_args, None

    if name == "read_file":
        return {**default_args, "filePath": rel_player}, None

    if name == "write_file":
        return {**default_args, "filePath": "tmp/smoke.txt", "content": "smoke"}, None

    if name == "delete_file":
        return {**default_args, "filePath": "tmp/smoke.txt"}, None

    if name == "create_directory":
        return {**default_args, "directoryPath": "tmp/new_dir"}, None

    if name == "list_project_files":
        return default_args, None

    if name == "read_project_settings":
        return default_args, None

    if name == "modify_project_settings":
        return {**default_args, "key": "application/config/name", "value": '"SmokeProjectModified"'}, None

    if name == "rename_file":
        return {**default_args, "from": "Scripts/Player.cs", "to": "Scripts/PlayerRenamed.cs"}, None

    if name == "set_main_scene":
        return {**default_args, "mainScene": "res://scenes/SmokeMain.tscn"}, None

    if name == "manage_autoloads":
        return {**default_args, "action": "list"}, None

    if name == "manage_input_map":
        return {**default_args, "action": "list"}, None

    if name == "manage_export_presets":
        return {**default_args, "action": "add", "presetName": "Linux/X11", "platform": "Linux/X11", "exportPath": "build/smoke.x86_64"}, None

    if name == "manage_layers":
        return {**default_args, "action": "list"}, None

    if name == "manage_plugins":
        return {**default_args, "action": "list"}, None

    if name == "manage_shader":
        return {**default_args, "action": "create", "shaderPath": "Shaders/Smoke.gdshader"}, None

    if name == "manage_translations":
        return {**default_args, "action": "list"}, None

    if name == "create_script":
        return {
            **default_args,
            "scriptPath": "Scripts/SmokeScript.cs",
            "className": "SmokeScript",
            "baseClass": "Node",
            "namespace": "SmokeProject.Game",
        }, None

    if name == "export_project":
        return {**default_args, "presetName": "Linux/X11", "outputPath": "build/smoke.x86_64", "debug": True}, None

    if name == "manage_ci_pipeline":
        return {**default_args, "action": "create"}, None

    if name == "manage_docker_export":
        return {**default_args, "action": "create"}, None

    if name == "attach_script":
        return {**default_args, "scenePath": rel_main_scene, "nodePath": "Main", "scriptPath": rel_player}, None

    if name == "read_scene":
        return {**default_args, "scenePath": rel_main_scene}, None

    if name == "modify_scene_node":
        return {
            **default_args,
            "scenePath": rel_main_scene,
            "nodePath": "Main",
            "properties": {"name": "Main"},
        }, None

    if name == "remove_scene_node":
        return {**default_args, "scenePath": rel_main_scene, "nodePath": "Main/NoNode"}, None

    if name.startswith("game_"):
        args = dict(default_args)
        args["nodePath"] = root_path
        args["parentPath"] = root_path

        if name == "game_get_property":
            args["property"] = "name"
        if name == "game_set_property":
            args["property"] = "name"
            args["value"] = "SmokeMain"
        if name == "game_call_method":
            args["method"] = "get_name"
        if name == "game_get_node_info":
            args["nodePath"] = root_path
        if name == "game_remove_node":
            args["nodePath"] = root_path + "/TempNode"
        if name == "game_change_scene":
            args["scenePath"] = "res://scenes/SmokeMain.tscn"
        if name == "game_instantiate_scene":
            args["scenePath"] = "res://scenes/SmokeMain.tscn"
            args["parentPath"] = root_path
        if name == "game_connect_signal":
            args["nodePath"] = root_path
            args["signalName"] = "tree_entered"
            args["targetPath"] = root_path
            args["method"] = "get_name"
        if name == "game_disconnect_signal":
            args["nodePath"] = root_path
            args["signalName"] = "tree_entered"
            args["targetPath"] = root_path
            args["method"] = "get_name"
        if name == "game_emit_signal":
            args["nodePath"] = root_path
            args["signalName"] = "tree_entered"
        if name == "game_play_animation":
            args["nodePath"] = root_path + "/AnimationPlayer"
            args["action"] = "get_list"
        if name == "game_tween_property":
            args["nodePath"] = root_path
            args["property"] = "process_mode"
            args["finalValue"] = 0
        if name == "game_get_nodes_in_group":
            args["group"] = "nonexistent"
        if name == "game_find_nodes_by_class":
            args["className"] = "Node"
        if name == "game_reparent_node":
            args["nodePath"] = root_path + "/ToReparent"
            args["newParentPath"] = root_path + "/UIRoot"
        if name == "game_script":
            args["action"] = "attach"
            args["nodePath"] = root_path
            args["scriptPath"] = "res://Scripts/Player.cs"
        if name == "game_audio_play":
            args["nodePath"] = root_path + "/Audio3D"
            args["action"] = "stop"
        if name == "game_audio_spatial":
            args["nodePath"] = root_path + "/Audio3D"
            args["action"] = "get_info"
        if name == "game_animation_tree":
            args["nodePath"] = root_path + "/AnimationTree"
            args["action"] = "get_state"
        if name == "game_animation_control":
            args["nodePath"] = root_path + "/AnimationPlayer"
            args["action"] = "get_info"
        if name == "game_skeleton_ik":
            args["nodePath"] = root_path + "/SkeletonIK3D"
            args["action"] = "stop"
        if name == "game_bone_pose":
            args["nodePath"] = root_path + "/Skeleton3D"
            args["action"] = "list"
        if name == "game_gridmap":
            args["nodePath"] = root_path + "/GridMap"
            args["action"] = "get_used"
        if name == "game_tilemap":
            args["nodePath"] = root_path + "/TileMapLayer"
            args["action"] = "get_cell"
            args["x"] = 0
            args["y"] = 0
        if name == "game_set_shader_param":
            args["nodePath"] = root_path + "/MeshInstance3D"
            args["paramName"] = "albedo"
            args["value"] = {"r": 1, "g": 1, "b": 1, "a": 1}
        if name == "game_camera_attributes":
            args["action"] = "get"
        if name == "game_set_camera":
            args["action"] = "set"
            args["fov"] = 75
        if name == "game_get_camera":
            args["action"] = "get"
        if name == "game_canvas":
            args["action"] = "create_layer"
        if name == "game_light_2d":
            args["action"] = "create_point"
        if name == "game_light_3d":
            args["action"] = "create"
            args["lightType"] = "directional"
        if name == "game_parallax":
            args["action"] = "create_background"
        if name == "game_path_2d":
            args["action"] = "create"
        if name == "game_path_3d":
            args["action"] = "create"
        if name == "game_multimesh":
            args["action"] = "create"
        if name == "game_csg":
            args["action"] = "create"
            args["csgType"] = "box"
        if name == "game_navigation_3d":
            args["action"] = "create"
        if name == "game_physics_2d":
            args["action"] = "ray"
            args["from"] = {"x": 0, "y": 0}
            args["to"] = {"x": 10, "y": 10}
        if name == "game_physics_3d":
            args["action"] = "ray"
            args["from"] = {"x": 0, "y": 0, "z": 0}
            args["to"] = {"x": 0, "y": 0, "z": 10}
        if name == "game_input_action":
            args["action"] = "list"
        if name == "game_input_state":
            args["action"] = "query"
        if name == "game_touch":
            args["action"] = "press"
            args["x"] = 10
            args["y"] = 10
        if name == "game_websocket":
            args["action"] = "status"
        if name == "game_multiplayer":
            args["action"] = "status"
        if name == "game_resource":
            args["action"] = "load"
            args["path"] = "res://icon.svg"
        if name == "game_spawn_node":
            args["type"] = "Node"
            args["name"] = "TempNode"
            args["parentPath"] = root_path
        if name == "game_add_collision":
            args["parentPath"] = root_path
            args["shapeType"] = "box"
        if name == "game_create_animation":
            args["nodePath"] = root_path + "/AnimationPlayer"
            args["animationName"] = "SmokeAnim"
        if name == "game_create_joint":
            args["parentPath"] = root_path
            args["jointType"] = "pin_3d"
        if name == "game_navigate_path":
            args["start"] = {"x": 0, "y": 0, "z": 0}
            args["end"] = {"x": 1, "y": 0, "z": 1}
        if name == "game_ui_control":
            args["nodePath"] = root_path + "/UIRoot"
            args["action"] = "get_info"
        if name == "game_ui_text":
            args["nodePath"] = root_path + "/UIRoot/LineEdit"
            args["action"] = "get"
        if name == "game_ui_popup":
            args["nodePath"] = root_path + "/UIRoot/PopupPanel"
            args["action"] = "get_info"
        if name == "game_ui_tree":
            args["nodePath"] = root_path + "/UIRoot/Tree"
            args["action"] = "get_items"
        if name == "game_ui_item_list":
            args["nodePath"] = root_path + "/UIRoot/ItemList"
            args["action"] = "get_items"
        if name == "game_ui_tabs":
            args["nodePath"] = root_path + "/UIRoot/TabContainer"
            args["action"] = "get_tabs"
        if name == "game_ui_menu":
            args["nodePath"] = root_path + "/UIRoot/PopupMenu"
            args["action"] = "get_items"
        if name == "game_ui_range":
            args["nodePath"] = root_path + "/UIRoot/ProgressBar"
            args["action"] = "get"
        if name == "game_debug_draw":
            args["action"] = "line"
            args["from"] = {"x": 0, "y": 0, "z": 0}
            args["to"] = {"x": 1, "y": 1, "z": 1}
        if name == "game_eval":
            args["code"] = "return 1 + 1"
        if name == "game_3d_effects":
            args["parentPath"] = root_path
            args["effectType"] = "reflection_probe"
        if name == "game_video":
            args["action"] = "create"
            args["parentPath"] = root_path
        if name == "game_visual_shader":
            args["action"] = "create"
            args["parentPath"] = root_path
        if name == "game_terrain":
            args["action"] = "create"
            args["parentPath"] = root_path

        if name == "game_set_property":
            args["property"] = "name"
            args["value"] = "SmokeMain"
        if name in {"game_connect_signal", "game_disconnect_signal", "game_emit_signal", "game_await_signal"}:
            args["signalName"] = "tree_entered"
        if name in {"game_key_press", "game_key_hold", "game_key_release"}:
            args["key"] = "Space"
        if name in {"game_mouse_move", "game_click", "game_scroll"}:
            args["x"] = 100
            args["y"] = 100
        if name == "game_wait":
            args["frames"] = 1
        if name in {"game_pause", "game_time_scale"}:
            args["action"] = "get"
        if name in {"game_window", "game_world_settings", "game_render_settings"}:
            args["action"] = "get"
        if name in {"game_http_request"}:
            args["url"] = "https://example.com"
        if name in {"game_audio_effect", "game_audio_bus_layout"}:
            args.setdefault("action", "list")
        if name in {"game_locale"}:
            args.setdefault("action", "get")
        if name in {"game_manage_group"}:
            args.setdefault("action", "get_groups")
            args.setdefault("nodePath", root_path)
        if name in {"game_rpc"}:
            args.setdefault("action", "call")
            args.setdefault("method", "get_name")
            args.setdefault("nodePath", root_path)
        if name == "game_physics_body":
            args["nodePath"] = root_path + "/RigidBody3D"
        if name == "game_remove_node":
            args["nodePath"] = root_path + "/ToRemove"
        if name == "game_set_particles":
            args["nodePath"] = root_path + "/Particles3D"
            args["emitting"] = False
        if name == "game_shape_2d":
            args["nodePath"] = root_path + "/Line2D"
            args["action"] = "get_points"
        if name == "game_ui_theme":
            args["nodePath"] = root_path + "/UIRoot"
            args["overrides"] = {"colors": {"font_color": {"r": 1, "g": 1, "b": 1, "a": 1}}}
        return args, None

    # default non-runtime tool args
    return default_args, None


def assess_response(resp: Dict[str, Any]) -> Tuple[str, str]:
    if "error" in resp and resp["error"]:
        return "FAIL", f"rpc_error: {resp['error']}"

    result = resp.get("result", {})
    content = result.get("content", [])
    if not content:
        return "PASS", "no_content"

    text = content[0].get("text", "")
    if not text:
        return "PASS", "empty_text"

    try:
        payload = json.loads(text)
    except Exception:
        lowered = text.lower()
        if "error" in lowered and "[error]" in lowered:
            return "FAIL", text[:500]
        return "PASS", text[:500]

    if isinstance(payload, dict):
        if payload.get("success") is False:
            return "FAIL", json.dumps(payload, ensure_ascii=False)[:500]
        if isinstance(payload.get("exitCode"), int) and payload.get("exitCode") != 0:
            return "FAIL", json.dumps(payload, ensure_ascii=False)[:500]
        if "error" in payload and payload.get("error"):
            return "FAIL", json.dumps(payload, ensure_ascii=False)[:500]

    return "PASS", json.dumps(payload, ensure_ascii=False)[:500]


def main() -> int:
    parser = argparse.ArgumentParser(description="Smoke all MCP tools for godot-mono-mcp")
    parser.add_argument("--csproj", default="src/GodotMonoMcp/GodotMonoMcp.csproj", help="Path to MCP server csproj")
    parser.add_argument("--project", default="/tmp/godot_mono_smoke_project", help="Godot project path for smoke")
    parser.add_argument("--output-dir", default="./smoke-results", help="Output folder")
    parser.add_argument("--timeout", type=float, default=45.0, help="Per-call timeout seconds")
    parser.add_argument("--skip-runtime", action="store_true", help="Skip game_* runtime tools")
    args = parser.parse_args()

    root = Path.cwd()
    csproj = (root / args.csproj).resolve()
    project_path = Path(args.project).resolve()
    output_dir = (root / args.output_dir).resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    ensure_test_project(project_path)

    client = McpClient(csproj, timeout_sec=args.timeout)
    client.start()

    results: List[Dict[str, Any]] = []
    started_at = time.time()

    try:
        init = client.call("initialize")
        if "error" in init:
            raise RuntimeError(f"initialize failed: {init}")

        tools_resp = client.call("tools/list")
        if "error" in tools_resp:
            raise RuntimeError(f"tools/list failed: {tools_resp}")

        tools = [t["name"] for t in tools_resp.get("result", {}).get("tools", [])]
        tools.sort()

        runtime_ready = False
        if not args.skip_runtime:
            run_resp = client.call("tools/call", {
                "name": "run_project",
                "arguments": {
                    "projectPath": str(project_path),
                    "scene": "res://scenes/SmokeMain.tscn",
                },
            }, timeout_sec=max(args.timeout, 80.0))
            st, detail = assess_response(run_resp)
            runtime_ready = st == "PASS"
            results.append({
                "tool": "run_project(pre)",
                "status": st,
                "detail": detail,
            })

        for idx, name in enumerate(tools, start=1):
            if name == "run_project":
                continue
            if args.skip_runtime and name.startswith("game_"):
                results.append({"tool": name, "status": "SKIP", "detail": "--skip-runtime"})
                continue
            if name.startswith("game_") and not runtime_ready:
                results.append({"tool": name, "status": "SKIP", "detail": "runtime not ready"})
                continue

            try:
                tool_args, skip_reason = build_tool_args(name, project_path)
                if skip_reason:
                    results.append({"tool": name, "status": "SKIP", "detail": skip_reason})
                    continue

                resp = client.call("tools/call", {"name": name, "arguments": tool_args})
                status, detail = assess_response(resp)
                results.append({"tool": name, "status": status, "detail": detail})
            except Exception as exc:
                results.append({"tool": name, "status": "FAIL", "detail": str(exc)})

            print(f"[{idx:03d}/{len(tools)}] {name}: {results[-1]['status']}")

        try:
            client.call("tools/call", {"name": "stop_project", "arguments": {}}, timeout_sec=15)
        except Exception:
            pass

    finally:
        client.stop()

    duration = round(time.time() - started_at, 2)
    passed = sum(1 for x in results if x["status"] == "PASS")
    failed = sum(1 for x in results if x["status"] == "FAIL")
    skipped = sum(1 for x in results if x["status"] == "SKIP")

    summary = {
        "timestamp": int(time.time()),
        "durationSec": duration,
        "total": len(results),
        "pass": passed,
        "fail": failed,
        "skip": skipped,
        "projectPath": str(project_path),
        "csproj": str(csproj),
    }

    (output_dir / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    with (output_dir / "results.jsonl").open("w", encoding="utf-8") as f:
        for row in results:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")

    print("\n== Smoke Summary ==")
    print(json.dumps(summary, ensure_ascii=False, indent=2))
    print(f"Details: {output_dir / 'results.jsonl'}")

    return 1 if failed > 0 else 0


if __name__ == "__main__":
    sys.exit(main())

@tool
extends EditorPlugin
## DepthCam Editor Plugin
## Adds custom inspector for DepthCameraNode with device selection and preview

var _inspector_plugin: EditorInspectorPlugin
var _native_library_available := false


func _enter_tree() -> void:
	# Check if native library is available before loading dependent scripts
	if not _check_native_library():
		push_warning("[DepthCam] Native library not built. Run 'scons platform=<platform> arch=<arch>' in addons/gd-depthcam/")
		_show_build_instructions()
		return
	
	_native_library_available = true
	print("[DepthCam] Plugin loaded")
	
	# Add custom inspector for DepthCameraNode (only if native lib is available)
	var InspectorPlugin = load("res://addons/gd-depthcam/scripts/depth_camera_inspector.gd")
	if InspectorPlugin:
		_inspector_plugin = InspectorPlugin.new()
		add_inspector_plugin(_inspector_plugin)


func _exit_tree() -> void:
	if _inspector_plugin:
		remove_inspector_plugin(_inspector_plugin)
		_inspector_plugin = null
	
	if _native_library_available:
		print("[DepthCam] Plugin unloaded")


func _check_native_library() -> bool:
	# Check if DepthCameraNode class exists (registered by native library)
	return ClassDB.class_exists("DepthCameraNode")


func _show_build_instructions() -> void:
	# Show a helpful message in the editor
	var os_name := OS.get_name().to_lower()
	var arch := "arm64" if OS.has_feature("arm64") else "x86_64"
	
	var platform := "macos"
	if os_name == "windows":
		platform = "windows"
	elif os_name == "linux":
		platform = "linux"
	
	var cmd := "cd addons/gd-depthcam && scons platform=%s arch=%s -j8" % [platform, arch]
	
	printerr("")
	printerr("╔══════════════════════════════════════════════════════════════╗")
	printerr("║  gd-depthcam: Native library not found!                      ║")
	printerr("║                                                              ║")
	printerr("║  Build it with:                                              ║")
	printerr("║    %s" % cmd.rpad(55) + "║")
	printerr("║                                                              ║")
	printerr("║  Then restart the Godot Editor.                              ║")
	printerr("╚══════════════════════════════════════════════════════════════╝")
	printerr("")

@tool
extends EditorInspectorPlugin
class_name DepthCameraInspectorPlugin

const DeviceSelector = preload("res://addons/gd-depthcam/scripts/depth_camera_device_selector.gd")


func _can_handle(object: Object) -> bool:
	return object is DepthCameraNode


func _parse_begin(object: Object) -> void:
	var node := object as DepthCameraNode
	if not node:
		return
	
	# Add device selector at the top
	var device_editor := DeviceSelector.new()
	device_editor.setup(node)
	add_custom_control(device_editor)

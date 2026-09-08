@tool
extends PanelContainer
class_name DepthCameraFullInspector
## Full inspector panel combining device control and property editor
## Drop this into your UI for a complete depth camera control interface

const ControlPanelScript := preload("res://addons/gd-depthcam/scripts/ui/depth_camera_control_panel.gd")
const PropertyEditorScript := preload("res://addons/gd-depthcam/scripts/ui/depth_camera_property_editor.gd")

@export var title: String = "DepthCam Inspector":
	set(v):
		title = v
		if _title_label:
			_title_label.text = v

# Node reference
var _node: DepthCameraNode

# UI Components
var _scroll: ScrollContainer
var _vbox: VBoxContainer
var _title_label: Label
var _control_panel: VBoxContainer  # DepthCameraControlPanel
var _property_editor: VBoxContainer  # DepthCameraPropertyEditor


func _ready() -> void:
	_build_ui()


func _build_ui() -> void:
	custom_minimum_size.x = 280
	
	_scroll = ScrollContainer.new()
	_scroll.horizontal_scroll_mode = ScrollContainer.SCROLL_MODE_DISABLED
	_scroll.size_flags_vertical = Control.SIZE_EXPAND_FILL
	add_child(_scroll)
	
	_vbox = VBoxContainer.new()
	_vbox.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_vbox.add_theme_constant_override("separation", 8)
	_scroll.add_child(_vbox)
	
	# Title
	_title_label = Label.new()
	_title_label.text = title
	_title_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_title_label.add_theme_font_size_override("font_size", 16)
	_vbox.add_child(_title_label)
	
	_vbox.add_child(HSeparator.new())
	
	# Device label
	var device_label := Label.new()
	device_label.text = "Device"
	device_label.add_theme_font_size_override("font_size", 13)
	device_label.add_theme_color_override("font_color", Color(0.8, 0.9, 1.0))
	_vbox.add_child(device_label)
	
	# Control panel
	_control_panel = ControlPanelScript.new()
	_vbox.add_child(_control_panel)
	
	_vbox.add_child(HSeparator.new())
	
	# Property editor
	_property_editor = PropertyEditorScript.new()
	_vbox.add_child(_property_editor)


## Setup the inspector for a DepthCameraNode
func setup(node: DepthCameraNode) -> void:
	_node = node
	
	if _control_panel:
		_control_panel.setup(node)
	
	if _property_editor:
		_property_editor.setup(node)


## Get the control panel component
func get_control_panel() -> VBoxContainer:
	return _control_panel


## Get the property editor component
func get_property_editor() -> VBoxContainer:
	return _property_editor


## Refresh device list
func refresh_devices() -> void:
	if _control_panel:
		_control_panel.refresh_devices()


## Refresh property values
func refresh_properties() -> void:
	if _property_editor:
		_property_editor.refresh()


## Start streaming
func start_streaming() -> void:
	if _control_panel:
		_control_panel.start_streaming()


## Stop streaming
func stop_streaming() -> void:
	if _control_panel:
		_control_panel.stop_streaming()


## Check if streaming
func is_streaming() -> bool:
	return _control_panel.is_streaming() if _control_panel else false

@tool
extends PanelContainer
class_name DepthCameraVideoPanel
## Reusable video panel for displaying a single depth camera stream
## Can be used in editor tools or runtime applications

signal frame_received(texture: ImageTexture)
signal fullscreen_requested(panel: DepthCameraVideoPanel)
signal preview_toggled(enabled: bool)

@export var stream_name: String = "Video":
	set(v):
		stream_name = v
		if _title_label:
			_title_label.text = v

@export var show_resolution: bool = true:
	set(v):
		show_resolution = v
		if _res_label:
			_res_label.visible = v

@export var show_fullscreen_button: bool = true:
	set(v):
		show_fullscreen_button = v
		if _fullscreen_btn:
			_fullscreen_btn.visible = v

@export var preview_enabled: bool = true:
	set(v):
		preview_enabled = v
		if _preview_btn:
			_preview_btn.button_pressed = v
			_preview_btn.modulate = Color.WHITE if v else Color(0.5, 0.5, 0.5)
		if _view:
			_view.visible = v and _has_frame
		if _placeholder_label:
			_placeholder_label.visible = not v or not _has_frame
			if not v:
				_placeholder_label.text = "Preview disabled"
			else:
				_placeholder_label.text = placeholder_text

@export var placeholder_text: String = "No stream":
	set(v):
		placeholder_text = v
		if _placeholder_label:
			_placeholder_label.text = v

# UI elements
var _vbox: VBoxContainer
var _header: HBoxContainer
var _title_label: Label
var _res_label: Label
var _preview_btn: Button
var _fullscreen_btn: Button
var _content: Panel
var _view: TextureRect
var _placeholder_label: Label

# State
var _has_frame := false


func _ready() -> void:
	_build_ui()


func _build_ui() -> void:
	size_flags_horizontal = Control.SIZE_EXPAND_FILL
	size_flags_vertical = Control.SIZE_EXPAND_FILL
	
	_vbox = VBoxContainer.new()
	add_child(_vbox)
	
	# Header
	_header = HBoxContainer.new()
	_header.add_theme_constant_override("separation", 8)
	_vbox.add_child(_header)
	
	_title_label = Label.new()
	_title_label.text = stream_name
	_title_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_title_label.add_theme_font_size_override("font_size", 14)
	_header.add_child(_title_label)
	
	_res_label = Label.new()
	_res_label.visible = show_resolution
	_res_label.add_theme_font_size_override("font_size", 11)
	_res_label.add_theme_color_override("font_color", Color(0.5, 0.5, 0.5))
	_header.add_child(_res_label)
	
	_preview_btn = Button.new()
	_preview_btn.text = "👁"
	_preview_btn.tooltip_text = "Toggle Preview"
	_preview_btn.toggle_mode = true
	_preview_btn.button_pressed = preview_enabled
	_preview_btn.custom_minimum_size = Vector2(28, 24)
	_preview_btn.toggled.connect(_on_preview_toggled)
	_header.add_child(_preview_btn)
	
	_fullscreen_btn = Button.new()
	_fullscreen_btn.text = "⛶"
	_fullscreen_btn.tooltip_text = "Fullscreen"
	_fullscreen_btn.visible = show_fullscreen_button
	_fullscreen_btn.custom_minimum_size = Vector2(28, 24)
	_fullscreen_btn.pressed.connect(_on_fullscreen_pressed)
	_header.add_child(_fullscreen_btn)
	
	# Content area
	_content = Panel.new()
	_content.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_content.size_flags_vertical = Control.SIZE_EXPAND_FILL
	_content.custom_minimum_size = Vector2(160, 120)
	_vbox.add_child(_content)
	
	# Dark background style
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.08, 0.08, 0.08)
	_content.add_theme_stylebox_override("panel", style)
	
	# Video view
	_view = TextureRect.new()
	_view.set_anchors_preset(Control.PRESET_FULL_RECT)
	_view.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	_view.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	_view.visible = false
	_content.add_child(_view)
	
	# Placeholder
	_placeholder_label = Label.new()
	_placeholder_label.text = placeholder_text
	_placeholder_label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	_placeholder_label.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	_placeholder_label.set_anchors_preset(Control.PRESET_FULL_RECT)
	_placeholder_label.add_theme_color_override("font_color", Color(0.3, 0.3, 0.3))
	_content.add_child(_placeholder_label)


func _on_fullscreen_pressed() -> void:
	fullscreen_requested.emit(self)


func _on_preview_toggled(enabled: bool) -> void:
	preview_enabled = enabled
	preview_toggled.emit(enabled)


## Display a frame texture
func show_frame(texture: ImageTexture) -> void:
	if not _view:
		return
	
	_has_frame = true
	
	# Skip texture update if preview is disabled (performance optimization)
	if not preview_enabled:
		return
	
	_view.texture = texture
	_view.visible = true
	_placeholder_label.visible = false
	
	# Update resolution label
	if show_resolution and texture and texture.get_image():
		var img := texture.get_image()
		_res_label.text = "%dx%d" % [img.get_width(), img.get_height()]
	
	frame_received.emit(texture)


## Clear the display and show placeholder
func clear() -> void:
	if not _view:
		return
	
	_view.texture = null
	_view.visible = false
	_placeholder_label.visible = true
	_res_label.text = ""
	_has_frame = false


## Check if currently displaying a frame
func has_frame() -> bool:
	return _has_frame


## Get the current texture
func get_texture() -> ImageTexture:
	if _view and _view.texture:
		return _view.texture as ImageTexture
	return null


## Set preview enabled state (without emitting signal)
func set_preview_enabled_no_signal(enabled: bool) -> void:
	if _preview_btn:
		_preview_btn.set_pressed_no_signal(enabled)
	preview_enabled = enabled


## Show a status message overlay
func show_message(msg: String) -> void:
	if _placeholder_label:
		_placeholder_label.text = msg
		_placeholder_label.visible = true
		if _view:
			_view.visible = false


## Clear the status message overlay
func clear_message() -> void:
	if _placeholder_label:
		if _has_frame and preview_enabled:
			_placeholder_label.visible = false
			if _view:
				_view.visible = true
		else:
			_placeholder_label.text = placeholder_text

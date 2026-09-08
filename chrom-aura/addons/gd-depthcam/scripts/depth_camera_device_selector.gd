@tool
extends VBoxContainer
class_name DepthCameraDeviceSelector
## Custom inspector control for DepthCameraNode
## Provides device selection and optional live preview in the editor

# Constants
const PREVIEW_HEIGHT := 100
const SECTION_MARGIN := 4

# Node reference
var _node: DepthCameraNode

# UI Elements
var _device_row: HBoxContainer
var _device_dropdown: OptionButton
var _refresh_btn: Button

var _preview_section: VBoxContainer
var _preview_toggle: CheckButton
var _preview_views: HBoxContainer
var _status_bar: HBoxContainer
var _status_label: Label
var _fps_label: Label

var _rgb_view: TextureRect
var _depth_view: TextureRect
var _ir_view: TextureRect

# State
var _is_previewing := false
var _frame_count := 0
var _last_fps_time := 0.0
var _was_previewing_before_play := false  # Track if we need to resume after game stops


func setup(node: DepthCameraNode) -> void:
	_node = node
	_build_ui()
	_refresh_devices()
	_connect_signals()
	_connect_editor_signals()
	
	# Always run _process to detect game state changes
	set_process(true)


func _build_ui() -> void:
	# Use consistent spacing
	add_theme_constant_override("separation", SECTION_MARGIN)
	
	# === Device Selection Row ===
	_device_row = HBoxContainer.new()
	add_child(_device_row)
	
	_device_dropdown = OptionButton.new()
	_device_dropdown.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_device_dropdown.tooltip_text = "Select depth camera device"
	_device_dropdown.item_selected.connect(_on_device_selected)
	_device_row.add_child(_device_dropdown)
	
	_refresh_btn = Button.new()
	_refresh_btn.text = "⟳"
	_refresh_btn.tooltip_text = "Refresh device list"
	_refresh_btn.custom_minimum_size.x = 28
	_refresh_btn.pressed.connect(_refresh_devices)
	_device_row.add_child(_refresh_btn)
	
	# === Preview Section ===
	_preview_section = VBoxContainer.new()
	_preview_section.add_theme_constant_override("separation", 2)
	add_child(_preview_section)
	
	# Preview header with toggle
	var preview_header := HBoxContainer.new()
	_preview_section.add_child(preview_header)
	
	_preview_toggle = CheckButton.new()
	_preview_toggle.text = "Editor Preview"
	_preview_toggle.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_preview_toggle.toggled.connect(_on_preview_toggled)
	preview_header.add_child(_preview_toggle)
	
	# Preview views container (initially hidden)
	_preview_views = HBoxContainer.new()
	_preview_views.visible = false
	_preview_views.add_theme_constant_override("separation", 4)
	_preview_section.add_child(_preview_views)
	
	# Create stream views
	_rgb_view = _create_preview_view("RGB")
	_depth_view = _create_preview_view("Depth")
	_ir_view = _create_preview_view("IR")
	
	# Status bar
	_status_bar = HBoxContainer.new()
	_status_bar.visible = false
	_preview_section.add_child(_status_bar)
	
	_status_label = Label.new()
	_status_label.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_status_label.add_theme_color_override("font_color", Color(0.6, 0.6, 0.6))
	_status_label.add_theme_font_size_override("font_size", 11)
	_status_bar.add_child(_status_label)
	
	_fps_label = Label.new()
	_fps_label.add_theme_color_override("font_color", Color(0.5, 0.8, 0.5))
	_fps_label.add_theme_font_size_override("font_size", 11)
	_status_bar.add_child(_fps_label)


func _create_preview_view(title: String) -> TextureRect:
	var container := VBoxContainer.new()
	container.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_preview_views.add_child(container)
	
	var label := Label.new()
	label.text = title
	label.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	label.add_theme_font_size_override("font_size", 10)
	label.add_theme_color_override("font_color", Color(0.6, 0.6, 0.6))
	container.add_child(label)
	
	var view := TextureRect.new()
	view.custom_minimum_size = Vector2(0, PREVIEW_HEIGHT)
	view.expand_mode = TextureRect.EXPAND_IGNORE_SIZE
	view.stretch_mode = TextureRect.STRETCH_KEEP_ASPECT_CENTERED
	view.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	container.add_child(view)
	
	# Placeholder styling
	var panel := Panel.new()
	panel.custom_minimum_size = Vector2(0, PREVIEW_HEIGHT)
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.12, 0.12, 0.12)
	style.set_corner_radius_all(3)
	panel.add_theme_stylebox_override("panel", style)
	container.add_child(panel)
	
	var placeholder := Label.new()
	placeholder.text = "—"
	placeholder.horizontal_alignment = HORIZONTAL_ALIGNMENT_CENTER
	placeholder.vertical_alignment = VERTICAL_ALIGNMENT_CENTER
	placeholder.anchors_preset = Control.PRESET_FULL_RECT
	placeholder.add_theme_color_override("font_color", Color(0.3, 0.3, 0.3))
	panel.add_child(placeholder)
	
	view.visible = false
	panel.visible = true
	
	# Store references for easy access
	view.set_meta("placeholder", panel)
	
	return view


func _connect_signals() -> void:
	if not _node:
		return
	
	if _node.has_signal("rgb_frame_ready"):
		if not _node.rgb_frame_ready.is_connected(_on_rgb_frame):
			_node.rgb_frame_ready.connect(_on_rgb_frame)
	if _node.has_signal("depth_frame_ready"):
		if not _node.depth_frame_ready.is_connected(_on_depth_frame):
			_node.depth_frame_ready.connect(_on_depth_frame)
	if _node.has_signal("ir_frame_ready"):
		if not _node.ir_frame_ready.is_connected(_on_ir_frame):
			_node.ir_frame_ready.connect(_on_ir_frame)


func _refresh_devices() -> void:
	if not _device_dropdown:
		return
	
	_device_dropdown.clear()
	
	var manager := Engine.get_singleton("DepthCameraManager")
	var devices: Array = []
	
	if manager:
		devices = manager.enumerate_devices()
	elif ClassDB.class_exists("DepthCameraNode"):
		devices = DepthCameraNode.enumerate_devices()
	
	if devices.is_empty():
		_device_dropdown.add_item("No devices found")
		_device_dropdown.set_item_disabled(0, true)
		_preview_toggle.disabled = true
		return
	
	# Get current device_id from node (set via inspector or scene)
	var current_id := ""
	if _node:
		current_id = _node.get_device_id()
	
	var select_idx := -1
	
	for i in devices.size():
		var dev: Dictionary = devices[i]
		var device_id: String = dev.get("device_id", "")
		var name: String = dev.get("display_name", "Device")
		var backend: String = dev.get("backend_type", "")
		
		_device_dropdown.add_item("%s (%s)" % [name, backend])
		_device_dropdown.set_item_metadata(i, device_id)
		
		if device_id == current_id:
			select_idx = i
	
	# If no match found, default to first device
	if select_idx < 0:
		select_idx = 0
	
	_device_dropdown.select(select_idx)
	_preview_toggle.disabled = false


func _on_device_selected(idx: int) -> void:
	if not _node:
		return
	
	var device_id: String = _device_dropdown.get_item_metadata(idx)
	_node.set_device_id(device_id)
	_mark_modified()
	
	# Restart preview if active
	if _is_previewing:
		_stop_preview()
		_start_preview()


func _on_preview_toggled(enabled: bool) -> void:
	if enabled:
		_start_preview()
	else:
		_stop_preview()


func _start_preview() -> void:
	if not _node:
		return
	
	var device_id := _node.get_device_id()
	if device_id.is_empty() and _device_dropdown.item_count > 0:
		device_id = _device_dropdown.get_item_metadata(0)
		_node.set_device_id(device_id)
	
	if device_id.is_empty():
		_set_status("No device available")
		_preview_toggle.set_pressed_no_signal(false)
		return
	
	# Open and start
	if not _node.open_device_by_id(device_id):
		_set_status("Failed to open device")
		_preview_toggle.set_pressed_no_signal(false)
		return
	
	if not _node.start_streaming():
		_set_status("Failed to start stream")
		_node.close_device()
		_preview_toggle.set_pressed_no_signal(false)
		return
	
	_is_previewing = true
	_preview_views.visible = true
	_status_bar.visible = true
	_set_status("Streaming")
	_frame_count = 0
	_last_fps_time = Time.get_ticks_msec() / 1000.0


func _stop_preview() -> void:
	if _node:
		_node.stop_streaming()
		_node.close_device()
	
	_is_previewing = false
	_preview_views.visible = false
	_status_bar.visible = false
	
	_clear_views()


func _clear_views() -> void:
	for view in [_rgb_view, _depth_view, _ir_view]:
		if view:
			view.texture = null
			view.visible = false
			var placeholder: Control = view.get_meta("placeholder")
			if placeholder:
				placeholder.visible = true


func _show_frame(view: TextureRect, texture: ImageTexture) -> void:
	if not view or not texture:
		return
	
	view.texture = texture
	view.visible = true
	
	var placeholder: Control = view.get_meta("placeholder")
	if placeholder:
		placeholder.visible = false


func _on_rgb_frame(texture: ImageTexture) -> void:
	if _is_previewing and is_instance_valid(_rgb_view):
		_show_frame(_rgb_view, texture)
		_update_fps()


func _on_depth_frame(texture: ImageTexture) -> void:
	if _is_previewing and is_instance_valid(_depth_view):
		_show_frame(_depth_view, texture)


func _on_ir_frame(texture: ImageTexture) -> void:
	if _is_previewing and is_instance_valid(_ir_view):
		_show_frame(_ir_view, texture)


func _update_fps() -> void:
	_frame_count += 1
	var now := Time.get_ticks_msec() / 1000.0
	var elapsed := now - _last_fps_time
	
	if elapsed >= 1.0:
		var fps := _frame_count / elapsed
		_frame_count = 0
		_last_fps_time = now
		_fps_label.text = "%.0f fps" % fps


func _set_status(text: String) -> void:
	if _status_label:
		_status_label.text = text


func _mark_modified() -> void:
	var scene := EditorInterface.get_edited_scene_root()
	if scene:
		scene.notify_property_list_changed()


func _process(_delta: float) -> void:
	# Check if game started/stopped (to release device)
	_check_game_running()
	
	if _is_previewing and _node and is_instance_valid(_node):
		if _node.has_method("poll_frames"):
			_node.poll_frames()


func _notification(what: int) -> void:
	if what == NOTIFICATION_PREDELETE:
		if _is_previewing:
			_stop_preview()


func _connect_editor_signals() -> void:
	# Connect to editor play/stop signals to release device when game runs
	if not Engine.is_editor_hint():
		return
	
	var editor := EditorInterface.get_editor_main_screen()
	if not editor:
		return
	
	# Use EditorInterface to detect play state changes
	# We check this in _process instead since there's no direct signal
	pass


func _check_game_running() -> void:
	"""Stop preview if game is running to avoid device conflicts"""
	if not Engine.is_editor_hint():
		return
	
	var is_game_running := EditorInterface.is_playing_scene()
	
	if is_game_running and _is_previewing:
		# Game started - stop preview and remember state
		_was_previewing_before_play = true
		_stop_preview()
		_set_status("Paused (game running)")
		_preview_toggle.set_pressed_no_signal(false)
		_preview_toggle.disabled = true
		_status_bar.visible = true
	elif not is_game_running and _preview_toggle.disabled:
		# Game stopped - re-enable toggle
		_preview_toggle.disabled = false
		_set_status("Game stopped")
		# Optionally auto-resume preview
		if _was_previewing_before_play:
			_was_previewing_before_play = false
			# Don't auto-restart, just enable the toggle

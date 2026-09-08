@tool
extends VBoxContainer
class_name DepthCameraControlPanel
## Control panel for DepthCameraNode with device selection and streaming controls
## Use this component in your UI to control a depth camera

signal device_changed(device_id: String)
signal streaming_started
signal streaming_stopped
signal error_occurred(message: String)

# Node reference
var _node: DepthCameraNode

# UI Elements
var _device_row: HBoxContainer
var _device_dropdown: OptionButton
var _refresh_btn: Button
var _open_device_label: Label
var _btn_row: HBoxContainer
var _btn_start: Button
var _btn_stop: Button
var _status_label: Label

# State
var _devices: Array = []
var _is_streaming := false
var _manager: Object = null


func _ready() -> void:
	_build_ui()
	_connect_manager()


func _build_ui() -> void:
	add_theme_constant_override("separation", 8)
	
	# Device selection row
	_device_row = HBoxContainer.new()
	add_child(_device_row)
	
	_device_dropdown = OptionButton.new()
	_device_dropdown.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_device_dropdown.item_selected.connect(_on_device_selected)
	_device_row.add_child(_device_dropdown)
	
	_refresh_btn = Button.new()
	_refresh_btn.text = "⟳"
	_refresh_btn.tooltip_text = "Refresh device list"
	_refresh_btn.pressed.connect(refresh_devices)
	_device_row.add_child(_refresh_btn)
	
	# Open device indicator (shows which device is currently streaming)
	_open_device_label = Label.new()
	_open_device_label.add_theme_font_size_override("font_size", 12)
	_open_device_label.add_theme_color_override("font_color", Color(0.4, 0.9, 0.4))
	_open_device_label.text = ""
	add_child(_open_device_label)
	
	# Control buttons
	_btn_row = HBoxContainer.new()
	add_child(_btn_row)
	
	_btn_start = Button.new()
	_btn_start.text = "▶ Start"
	_btn_start.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_btn_start.pressed.connect(start_streaming)
	_btn_row.add_child(_btn_start)
	
	_btn_stop = Button.new()
	_btn_stop.text = "⏹ Stop"
	_btn_stop.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	_btn_stop.disabled = true
	_btn_stop.pressed.connect(stop_streaming)
	_btn_row.add_child(_btn_stop)
	
	# Status label
	_status_label = Label.new()
	_status_label.add_theme_font_size_override("font_size", 12)
	_status_label.add_theme_color_override("font_color", Color(0.8, 0.8, 0.8))
	_status_label.text = "Ready"
	add_child(_status_label)


func _connect_manager() -> void:
	_manager = Engine.get_singleton("DepthCameraManager")
	if _manager and _manager.has_signal("devices_changed"):
		if not _manager.devices_changed.is_connected(_on_devices_changed):
			_manager.devices_changed.connect(_on_devices_changed)


## Setup the control panel for a DepthCameraNode
func setup(node: DepthCameraNode) -> void:
	_node = node
	
	if _node:
		# Connect to node signals
		if _node.has_signal("streaming_started"):
			if not _node.streaming_started.is_connected(_on_streaming_started):
				_node.streaming_started.connect(_on_streaming_started)
		if _node.has_signal("streaming_stopped"):
			if not _node.streaming_stopped.is_connected(_on_streaming_stopped):
				_node.streaming_stopped.connect(_on_streaming_stopped)
		if _node.has_signal("error_occurred"):
			if not _node.error_occurred.is_connected(_on_error):
				_node.error_occurred.connect(_on_error)
	
	# Refresh devices and sync selection
	refresh_devices()
	
	# Sync streaming state (in case auto_start already started streaming)
	_sync_streaming_state()


## Refresh the device list (force rescan)
func refresh_devices() -> void:
	_device_dropdown.clear()
	_devices.clear()
	
	if _manager:
		_devices = _manager.refresh_devices()  # Force rescan
	
	_populate_dropdown()


func _on_devices_changed(devices: Array) -> void:
	"""Called when DepthCameraManager detects device changes."""
	_devices = devices
	_populate_dropdown()


func _populate_dropdown() -> void:
	"""Populate dropdown from cached devices."""
	_device_dropdown.clear()
	
	if _devices.is_empty():
		_device_dropdown.add_item("No devices found")
		_device_dropdown.set_item_disabled(0, true)
		_btn_start.disabled = true
		set_status("No devices")
		return
	
	# Get current device_id from node
	var current_id := ""
	if _node:
		current_id = _node.get_device_id()
	
	var select_idx := -1
	
	for i: int in _devices.size():
		var dev: Dictionary = _devices[i]
		var device_id: String = dev.get("device_id", "")
		var serial: String = dev.get("serial_number", "")
		var name: String = dev.get("display_name", "Device")
		var backend: String = dev.get("backend_type", "")
		
		# Show serial number (truncated) for device identification
		var display_serial := ""
		if not serial.is_empty():
			# Show last 6 chars of serial for identification
			display_serial = " (" + serial.right(6) + ")"
		
		_device_dropdown.add_item("%s%s [%s]" % [name, display_serial, backend])
		_device_dropdown.set_item_metadata(i, device_id)
		
		# Match using manager's find_device for consistent matching
		if _manager and _manager.has_method("find_device"):
			var found: Dictionary = _manager.find_device(current_id)
			if not found.is_empty() and found.get("device_id", "") == device_id:
				select_idx = i
		else:
			# Fallback: direct match by device_id or serial
			if device_id == current_id or serial == current_id:
				select_idx = i
			# Also check if current_id contains the serial (legacy format)
			elif current_id.contains(":") and current_id.ends_with(serial) and not serial.is_empty():
				select_idx = i
	
	# If no match found, default to first device
	if select_idx < 0:
		select_idx = 0
	
	_device_dropdown.select(select_idx)
	_btn_start.disabled = false
	set_status("Found %d device(s)" % _devices.size())


## Sync UI state with the node's current streaming state
func _sync_streaming_state() -> void:
	if not _node:
		_is_streaming = false
		_btn_start.disabled = _devices.is_empty()
		_btn_stop.disabled = true
		_open_device_label.text = ""
		return
	
	# Check if node is already streaming (e.g., from auto_start)
	var streaming := _node.is_streaming() if _node.has_method("is_streaming") else false
	
	if streaming:
		_is_streaming = true
		_btn_start.disabled = true
		_btn_stop.disabled = false
		_update_open_device_label()
		set_status("Streaming")
	else:
		_is_streaming = false
		_btn_start.disabled = _devices.is_empty()
		_btn_stop.disabled = true
		_open_device_label.text = ""
		set_status("Ready")


## Start streaming from the selected device
func start_streaming() -> void:
	if not _node or _devices.is_empty():
		set_status("No device")
		return
	
	var idx := _device_dropdown.selected
	if idx < 0:
		idx = 0
		_device_dropdown.select(0)
	
	var device_id: String = _device_dropdown.get_item_metadata(idx)
	
	if not _node.open_device_by_id(device_id):
		set_status("Failed to open device")
		error_occurred.emit("Failed to open device")
		return
	
	if not _node.start_streaming():
		set_status("Failed to start")
		_node.close_device()
		error_occurred.emit("Failed to start streaming")
		return
	
	set_status("Starting...")


## Stop streaming
func stop_streaming() -> void:
	if _node:
		_node.stop_streaming()
		_node.close_device()


## Set status text
func set_status(text: String) -> void:
	if _status_label:
		_status_label.text = text


## Check if currently streaming
func is_streaming() -> bool:
	return _is_streaming


## Get the selected device ID
func get_selected_device_id() -> String:
	var idx := _device_dropdown.selected
	if idx >= 0 and idx < _devices.size():
		return _device_dropdown.get_item_metadata(idx)
	return ""


func _on_device_selected(idx: int) -> void:
	if idx < 0 or idx >= _devices.size():
		return
	
	var device_id: String = _device_dropdown.get_item_metadata(idx)
	
	if _node:
		_node.set_device_id(device_id)
	
	device_changed.emit(device_id)


func _on_streaming_started() -> void:
	_is_streaming = true
	_btn_start.disabled = true
	_btn_stop.disabled = false
	_update_open_device_label()
	set_status("Streaming")
	streaming_started.emit()


func _on_streaming_stopped() -> void:
	_is_streaming = false
	_btn_start.disabled = false
	_btn_stop.disabled = true
	_open_device_label.text = ""
	set_status("Stopped")
	streaming_stopped.emit()


func _on_error(msg: String) -> void:
	set_status("Error: " + msg)
	error_occurred.emit(msg)


func _process(_delta: float) -> void:
	# Update FPS display while streaming
	if _is_streaming and _node and _node.has_method("get_fps"):
		set_status("%.0f fps" % _node.get_fps())
	
	# Sync streaming state from node (handles changes made via editor inspector)
	if _node:
		var node_streaming := _node.is_streaming() if _node.has_method("is_streaming") else false
		if node_streaming != _is_streaming:
			if node_streaming:
				_on_streaming_started()
			else:
				_on_streaming_stopped()


## Update the open device label to show current device
func _update_open_device_label() -> void:
	if not _node or not _open_device_label:
		return
	
	var device_name := ""
	if _node.has_method("get_device_name"):
		device_name = _node.get_device_name()
	
	var device_id := ""
	if _node.has_method("get_device_id"):
		device_id = _node.get_device_id()
	
	# Extract serial from device_id (format: backend:serial)
	var serial := ""
	if device_id.contains(":"):
		serial = device_id.get_slice(":", 1)
	else:
		serial = device_id
	
	# Show truncated serial (last 6 chars) for identification
	var display_serial := ""
	if not serial.is_empty():
		display_serial = serial.right(6)
	
	if not device_name.is_empty() and not display_serial.is_empty():
		_open_device_label.text = "● Open: %s (%s)" % [device_name, display_serial]
	elif not device_name.is_empty():
		_open_device_label.text = "● Open: %s" % device_name
	elif not display_serial.is_empty():
		_open_device_label.text = "● Open: %s" % display_serial
	else:
		_open_device_label.text = "● Device open"

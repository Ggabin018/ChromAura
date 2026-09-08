@tool
extends VBoxContainer
class_name DepthCameraPropertyEditor
## Property editor panel for DepthCameraNode with proper category separators
## Mirrors the Godot editor inspector style

signal property_changed(property_name: String, value: Variant)

# Properties to skip (internal/inherited)
const SKIP_PROPS := [
	"script", "process_mode", "process_priority", "process_physics_priority",
	"process_thread_group", "process_thread_group_order", "process_thread_messages",
	"physics_interpolation_mode", "auto_translate_mode", "editor_description",
	"unique_name_in_owner", "scene_file_path", "owner", "multiplayer", "name"
]

# Categories to skip (base Node properties)
const SKIP_CATEGORIES := ["Node"]

# Node reference
var _node: DepthCameraNode
var _prop_controls: Dictionary = {}  # property_name -> Control


func _ready() -> void:
	add_theme_constant_override("separation", 0)


## Setup the editor for a DepthCameraNode
func setup(node: DepthCameraNode) -> void:
	_node = node
	rebuild()


## Rebuild all property controls
func rebuild() -> void:
	# Clear existing
	for child in get_children():
		child.queue_free()
	_prop_controls.clear()
	
	if not _node:
		return
	
	await get_tree().process_frame
	
	var props := _node.get_property_list()
	var current_category := ""
	var current_group := ""
	var first_prop := true
	
	for prop in props:
		var pname: String = prop.get("name", "")
		var ptype: int = prop.get("type", TYPE_NIL)
		var phint: int = prop.get("hint", PROPERTY_HINT_NONE)
		var phint_str: String = prop.get("hint_string", "")
		var pusage: int = prop.get("usage", 0)
		
		# Skip non-storage properties
		if not (pusage & PROPERTY_USAGE_STORAGE):
			# But check for category/group markers
			if pusage & PROPERTY_USAGE_CATEGORY:
				current_category = pname
				# Skip base Node category and its properties
				if pname in SKIP_CATEGORIES:
					continue
				_add_category_header(pname)
				first_prop = true
				continue
			elif pusage & PROPERTY_USAGE_GROUP:
				# Skip groups under skipped categories
				if current_category in SKIP_CATEGORIES:
					continue
				current_group = pname
				_add_group_header(pname)
				continue
			elif pusage & PROPERTY_USAGE_SUBGROUP:
				# Skip subgroups under skipped categories
				if current_category in SKIP_CATEGORIES:
					continue
				_add_subgroup_header(pname)
				continue
			continue
		
		# Skip internal properties and properties from skipped categories
		if pname.begins_with("_") or pname in SKIP_PROPS:
			continue
		
		# Skip all properties under skipped categories
		if current_category in SKIP_CATEGORIES:
			continue
		
		# Create property control
		var control := _create_prop_control(pname, ptype, phint, phint_str)
		if control:
			# Add separator between properties (but not before first)
			if not first_prop:
				var sep := HSeparator.new()
				sep.add_theme_constant_override("separation", 0)
				sep.modulate = Color(1, 1, 1, 0.1)
				add_child(sep)
			
			var row := _create_prop_row(pname, control)
			add_child(row)
			_prop_controls[pname] = control
			_sync_prop_from_node(pname, control)
			first_prop = false


func _add_category_header(category_name: String) -> void:
	"""Add a category header (like 'DepthCameraNode' in inspector)"""
	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_top", 8)
	margin.add_theme_constant_override("margin_bottom", 4)
	add_child(margin)
	
	var panel := PanelContainer.new()
	margin.add_child(panel)
	
	var style := StyleBoxFlat.new()
	style.bg_color = Color(0.18, 0.22, 0.28)
	style.set_corner_radius_all(3)
	style.content_margin_left = 8
	style.content_margin_right = 8
	style.content_margin_top = 4
	style.content_margin_bottom = 4
	panel.add_theme_stylebox_override("panel", style)
	
	var label := Label.new()
	label.text = category_name
	label.add_theme_font_size_override("font_size", 13)
	label.add_theme_color_override("font_color", Color(0.9, 0.9, 0.9))
	panel.add_child(label)


func _add_group_header(group_name: String) -> void:
	"""Add a group header (collapsible section in inspector)"""
	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_top", 8)
	margin.add_theme_constant_override("margin_bottom", 2)
	add_child(margin)
	
	var hbox := HBoxContainer.new()
	margin.add_child(hbox)
	
	# Expand icon placeholder
	var icon := Label.new()
	icon.text = "▼"
	icon.add_theme_font_size_override("font_size", 10)
	icon.add_theme_color_override("font_color", Color(0.6, 0.6, 0.6))
	hbox.add_child(icon)
	
	var label := Label.new()
	label.text = group_name
	label.add_theme_font_size_override("font_size", 12)
	label.add_theme_color_override("font_color", Color(0.7, 0.85, 1.0))
	hbox.add_child(label)


func _add_subgroup_header(subgroup_name: String) -> void:
	"""Add a subgroup header (indented under group)"""
	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_top", 4)
	margin.add_theme_constant_override("margin_bottom", 2)
	margin.add_theme_constant_override("margin_left", 16)
	add_child(margin)
	
	var label := Label.new()
	label.text = subgroup_name
	label.add_theme_font_size_override("font_size", 11)
	label.add_theme_color_override("font_color", Color(0.6, 0.75, 0.9))
	margin.add_child(label)


func _create_prop_row(pname: String, control: Control) -> HBoxContainer:
	var row := HBoxContainer.new()
	row.add_theme_constant_override("separation", 8)
	
	var margin := MarginContainer.new()
	margin.add_theme_constant_override("margin_left", 4)
	margin.add_theme_constant_override("margin_right", 4)
	margin.add_theme_constant_override("margin_top", 2)
	margin.add_theme_constant_override("margin_bottom", 2)
	row.add_child(margin)
	
	var inner := HBoxContainer.new()
	inner.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	margin.add_child(inner)
	
	var label := Label.new()
	label.text = _format_property_name(pname)
	label.custom_minimum_size.x = 120
	label.add_theme_font_size_override("font_size", 12)
	label.clip_text = true
	label.text_overrun_behavior = TextServer.OVERRUN_TRIM_ELLIPSIS
	inner.add_child(label)
	
	control.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	inner.add_child(control)
	
	return row


func _format_property_name(pname: String) -> String:
	"""Convert property_name to Property Name"""
	return pname.replace("_", " ").capitalize()


func _create_prop_control(pname: String, ptype: int, phint: int, hint_str: String) -> Control:
	match ptype:
		TYPE_BOOL:
			var cb := CheckBox.new()
			cb.toggled.connect(func(v: bool) -> void: _set_prop(pname, v))
			return cb
		
		TYPE_INT:
			if phint == PROPERTY_HINT_ENUM:
				var opt := OptionButton.new()
				for item in hint_str.split(","):
					opt.add_item(item.strip_edges())
				opt.item_selected.connect(func(i: int) -> void: _set_prop(pname, i))
				return opt
			else:
				return _create_spinbox(pname, phint, hint_str, true)
		
		TYPE_FLOAT:
			return _create_spinbox(pname, phint, hint_str, false)
		
		TYPE_STRING:
			if phint == PROPERTY_HINT_ENUM:
				# String enum - skip device_id, handled elsewhere
				if pname == "device_id":
					return null
				var opt := OptionButton.new()
				for item in hint_str.split(","):
					opt.add_item(item.strip_edges())
				opt.item_selected.connect(func(i: int) -> void: 
					_set_prop(pname, opt.get_item_text(i))
				)
				return opt
			var le := LineEdit.new()
			le.text_changed.connect(func(t: String) -> void: _set_prop(pname, t))
			return le
		
		TYPE_COLOR:
			var picker := ColorPickerButton.new()
			picker.edit_alpha = false
			picker.color_changed.connect(func(c: Color) -> void: _set_prop(pname, c))
			return picker
		
		TYPE_VECTOR2:
			return _create_vector2_control(pname)
		
		TYPE_VECTOR3:
			return _create_vector3_control(pname)
	
	return null


func _create_spinbox(pname: String, phint: int, hint_str: String, is_int: bool) -> SpinBox:
	var sb := SpinBox.new()
	sb.step = 1.0 if is_int else 0.01
	sb.min_value = -99999
	sb.max_value = 99999
	sb.allow_greater = true
	sb.allow_lesser = true
	
	if phint == PROPERTY_HINT_RANGE:
		var parts := hint_str.split(",")
		if parts.size() >= 2:
			sb.min_value = float(parts[0])
			sb.max_value = float(parts[1])
			sb.allow_greater = false
			sb.allow_lesser = false
		if parts.size() >= 3:
			sb.step = float(parts[2])
	
	sb.value_changed.connect(func(v: float) -> void:
		if is_int:
			_set_prop(pname, int(v))
		else:
			_set_prop(pname, v)
	)
	return sb


func _create_vector2_control(pname: String) -> HBoxContainer:
	var hbox := HBoxContainer.new()
	hbox.add_theme_constant_override("separation", 4)
	
	var x_sb := SpinBox.new()
	x_sb.step = 0.01
	x_sb.min_value = -99999
	x_sb.max_value = 99999
	x_sb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	x_sb.prefix = "x"
	hbox.add_child(x_sb)
	
	var y_sb := SpinBox.new()
	y_sb.step = 0.01
	y_sb.min_value = -99999
	y_sb.max_value = 99999
	y_sb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	y_sb.prefix = "y"
	hbox.add_child(y_sb)
	
	var update_func := func(_v: float) -> void:
		_set_prop(pname, Vector2(x_sb.value, y_sb.value))
	
	x_sb.value_changed.connect(update_func)
	y_sb.value_changed.connect(update_func)
	
	hbox.set_meta("x_spinbox", x_sb)
	hbox.set_meta("y_spinbox", y_sb)
	
	return hbox


func _create_vector3_control(pname: String) -> HBoxContainer:
	var hbox := HBoxContainer.new()
	hbox.add_theme_constant_override("separation", 4)
	
	var x_sb := SpinBox.new()
	x_sb.step = 0.01
	x_sb.min_value = -99999
	x_sb.max_value = 99999
	x_sb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	x_sb.prefix = "x"
	hbox.add_child(x_sb)
	
	var y_sb := SpinBox.new()
	y_sb.step = 0.01
	y_sb.min_value = -99999
	y_sb.max_value = 99999
	y_sb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	y_sb.prefix = "y"
	hbox.add_child(y_sb)
	
	var z_sb := SpinBox.new()
	z_sb.step = 0.01
	z_sb.min_value = -99999
	z_sb.max_value = 99999
	z_sb.size_flags_horizontal = Control.SIZE_EXPAND_FILL
	z_sb.prefix = "z"
	hbox.add_child(z_sb)
	
	var update_func := func(_v: float) -> void:
		_set_prop(pname, Vector3(x_sb.value, y_sb.value, z_sb.value))
	
	x_sb.value_changed.connect(update_func)
	y_sb.value_changed.connect(update_func)
	z_sb.value_changed.connect(update_func)
	
	hbox.set_meta("x_spinbox", x_sb)
	hbox.set_meta("y_spinbox", y_sb)
	hbox.set_meta("z_spinbox", z_sb)
	
	return hbox


func _set_prop(pname: String, value: Variant) -> void:
	if _node:
		_node.set(pname, value)
		property_changed.emit(pname, value)


func _sync_prop_from_node(pname: String, control: Control) -> void:
	if not _node:
		return
	
	var value: Variant = _node.get(pname)
	
	if control is CheckBox:
		control.set_pressed_no_signal(bool(value))
	elif control is OptionButton:
		if value is int:
			control.select(value)
		elif value is String:
			for i in control.item_count:
				if control.get_item_text(i) == value:
					control.select(i)
					break
	elif control is SpinBox:
		control.set_value_no_signal(float(value))
	elif control is LineEdit:
		control.text = str(value)
	elif control is ColorPickerButton:
		control.color = value as Color
	elif control is HBoxContainer:
		# Vector2 or Vector3
		if value is Vector2:
			var x_sb: SpinBox = control.get_meta("x_spinbox")
			var y_sb: SpinBox = control.get_meta("y_spinbox")
			if x_sb:
				x_sb.set_value_no_signal(value.x)
			if y_sb:
				y_sb.set_value_no_signal(value.y)
		elif value is Vector3:
			var x_sb: SpinBox = control.get_meta("x_spinbox")
			var y_sb: SpinBox = control.get_meta("y_spinbox")
			var z_sb: SpinBox = control.get_meta("z_spinbox")
			if x_sb:
				x_sb.set_value_no_signal(value.x)
			if y_sb:
				y_sb.set_value_no_signal(value.y)
			if z_sb:
				z_sb.set_value_no_signal(value.z)


## Refresh values from node
func refresh() -> void:
	for pname: String in _prop_controls:
		var control: Control = _prop_controls[pname]
		_sync_prop_from_node(pname, control)


## Get control for a specific property
func get_control(property_name: String) -> Control:
	return _prop_controls.get(property_name)


func _process(_delta: float) -> void:
	# Sync property values from node every frame (handles inspector changes)
	if _node and not _prop_controls.is_empty():
		for pname: String in _prop_controls:
			var control: Control = _prop_controls[pname]
			_sync_prop_from_node_if_changed(pname, control)


## Sync property only if value has changed (avoids UI flicker)
func _sync_prop_from_node_if_changed(pname: String, control: Control) -> void:
	if not _node:
		return
	
	var value: Variant = _node.get(pname)
	
	if control is CheckBox:
		if control.button_pressed != bool(value):
			control.set_pressed_no_signal(bool(value))
	elif control is OptionButton:
		var current_idx: int = control.selected
		var target_idx := -1
		if value is int:
			target_idx = value
		elif value is String:
			for i in control.item_count:
				if control.get_item_text(i) == value:
					target_idx = i
					break
		if target_idx >= 0 and current_idx != target_idx:
			control.select(target_idx)
	elif control is SpinBox:
		if not is_equal_approx(control.value, float(value)):
			control.set_value_no_signal(float(value))
	elif control is LineEdit:
		if control.text != str(value):
			control.text = str(value)
	elif control is ColorPickerButton:
		if control.color != (value as Color):
			control.color = value as Color
	elif control is HBoxContainer:
		# Vector2 or Vector3
		if value is Vector2:
			var x_sb: SpinBox = control.get_meta("x_spinbox")
			var y_sb: SpinBox = control.get_meta("y_spinbox")
			if x_sb and not is_equal_approx(x_sb.value, value.x):
				x_sb.set_value_no_signal(value.x)
			if y_sb and not is_equal_approx(y_sb.value, value.y):
				y_sb.set_value_no_signal(value.y)
		elif value is Vector3:
			var x_sb: SpinBox = control.get_meta("x_spinbox")
			var y_sb: SpinBox = control.get_meta("y_spinbox")
			var z_sb: SpinBox = control.get_meta("z_spinbox")
			if x_sb and not is_equal_approx(x_sb.value, value.x):
				x_sb.set_value_no_signal(value.x)
			if y_sb and not is_equal_approx(y_sb.value, value.y):
				y_sb.set_value_no_signal(value.y)
			if z_sb and not is_equal_approx(z_sb.value, value.z):
				z_sb.set_value_no_signal(value.z)

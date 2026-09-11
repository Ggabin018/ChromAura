extends CanvasLayer

const INSTRUCTION_FONT := preload("res://assets/fonts/LibreBaskerville-Italic.ttf")

@export var change_interval: float = 4.5

# Probability that all instructions are hidden
@export_range(0.0, 1.0)
var hide_probability: float = 0.05

# Prefix used to identify Easter Egg containers
@export var easter_egg_prefix: String = "EG"

# Probability of selecting an Easter Egg
@export_range(0.0, 1.0)
var easter_egg_probability: float = 0.3

# How long an Easter Egg remains visible
@export var easter_egg_duration: float = 1.0

# Popup appearance settings
@export var popup_fade_duration: float = 0.22
@export var popup_scale_duration: float = 0.28
@export var popup_transition_duration: float = 0.65
@export var popup_background_color := Color(0.0, 0.0, 0.0, 0.64)
@export var popup_border_width: int = 2
@export var popup_corner_radius: int = 26

@onready var instruction_container: Control = $DrawInstruction
@onready var gravity_status: PanelContainer = $GravityStatus
@onready var gravity_status_label: Label = $GravityStatus/Content/Label

var rng := RandomNumberGenerator.new()
var hide_egg_timer := Timer.new()

var last_instruction: BoxContainer = null
var current_instruction: BoxContainer = null
var next_regular_instruction_index := 0
var popup_style: StyleBoxFlat
var instruction_font: Font
var popup_base_position := Vector2.ZERO
var popup_tween: Tween
var gravity_status_style: StyleBoxFlat
var gravity_status_tween: Tween
var gravity_status_initialized := false
var gravity_status_enabled := false
var last_accent_index := -1
var accent_colors: Array[Color] = [
	Color("5CE1E6"), # cyan
	Color("FF75C3"), # rose
	Color("B7F34A"), # lime
	Color("FFBE5C"), # ambre
	Color("B69CFF"), # violet
]


func _ready() -> void:
	rng.randomize()
	popup_base_position = instruction_container.position

	_setup_popup_style()
	_setup_instruction_font()
	_setup_gravity_status_style()

	for child in instruction_container.get_children():
		if child is BoxContainer:
			child.visible = false

	instruction_container.visible = false
	instruction_container.modulate.a = 0.0
	instruction_container.scale = Vector2(0.96, 0.96)
	_apply_gravity_status(false)
	gravity_status.visible = false
	gravity_status.modulate.a = 0.0
	gravity_status_initialized = true

	hide_egg_timer.one_shot = true
	hide_egg_timer.timeout.connect(_hide_easter_egg)
	add_child(hide_egg_timer)

	show_next_instruction()

	var timer := Timer.new()
	timer.wait_time = change_interval
	timer.one_shot = false
	timer.autostart = true
	timer.timeout.connect(show_next_instruction)

	add_child(timer)


func _setup_popup_style() -> void:
	popup_style = StyleBoxFlat.new()

	popup_style.bg_color = popup_background_color
	popup_style.border_color = accent_colors[0]
	popup_style.border_width_left = popup_border_width
	popup_style.border_width_top = popup_border_width
	popup_style.border_width_right = popup_border_width
	popup_style.border_width_bottom = popup_border_width
	popup_style.shadow_size = 14
	popup_style.shadow_offset = Vector2.ZERO
	popup_style.corner_radius_top_left = popup_corner_radius
	popup_style.corner_radius_top_right = popup_corner_radius
	popup_style.corner_radius_bottom_left = popup_corner_radius
	popup_style.corner_radius_bottom_right = popup_corner_radius
	popup_style.corner_detail = 16
	popup_style.anti_aliasing = true
	popup_style.anti_aliasing_size = 1.5

	popup_style.content_margin_left = 16.0
	popup_style.content_margin_right = 16.0
	popup_style.content_margin_top = 9.0
	popup_style.content_margin_bottom = 9.0

	if instruction_container is PanelContainer:
		instruction_container.add_theme_stylebox_override(
			"panel",
			popup_style
		)

func _setup_instruction_font() -> void:
	instruction_font = INSTRUCTION_FONT


func _setup_gravity_status_style() -> void:
	gravity_status_style = StyleBoxFlat.new()
	gravity_status_style.bg_color = popup_background_color
	gravity_status_style.border_width_left = popup_border_width
	gravity_status_style.border_width_top = popup_border_width
	gravity_status_style.border_width_right = popup_border_width
	gravity_status_style.border_width_bottom = popup_border_width
	gravity_status_style.shadow_size = 9
	gravity_status_style.shadow_offset = Vector2.ZERO
	gravity_status_style.corner_radius_top_left = 14
	gravity_status_style.corner_radius_top_right = 14
	gravity_status_style.corner_radius_bottom_left = 14
	gravity_status_style.corner_radius_bottom_right = 14
	gravity_status_style.corner_detail = 12
	gravity_status_style.anti_aliasing = true
	gravity_status_style.anti_aliasing_size = 1.5
	gravity_status_style.content_margin_left = 12.0
	gravity_status_style.content_margin_right = 12.0
	gravity_status_style.content_margin_top = 2.0
	gravity_status_style.content_margin_bottom = 2.0
	gravity_status.add_theme_stylebox_override("panel", gravity_status_style)


func set_gravity_status(enabled: bool) -> void:
	if gravity_status_enabled == enabled and gravity_status_initialized:
		return

	if gravity_status_tween != null and gravity_status_tween.is_valid():
		gravity_status_tween.kill()

	if not gravity_status_initialized:
		_apply_gravity_status(enabled)
		gravity_status_initialized = true
		return

	_apply_gravity_status(enabled)
	gravity_status.visible = true
	gravity_status.modulate.a = 0.0
	gravity_status_tween = create_tween()
	gravity_status_tween.tween_property(gravity_status, "modulate:a", 1.0, 0.20)
	gravity_status_tween.tween_interval(3.0)
	gravity_status_tween.tween_property(gravity_status, "modulate:a", 0.0, 0.20)
	gravity_status_tween.tween_callback(func() -> void:
		gravity_status.visible = false
	)


func _apply_gravity_status(enabled: bool) -> void:
	gravity_status_enabled = enabled
	var accent := Color("B7F34A") if enabled else Color("FF75C3")
	gravity_status_style.border_color = accent
	gravity_status_style.shadow_color = Color(accent.r, accent.g, accent.b, 0.38)
	gravity_status_label.text = "Gravité ON" if enabled else "Gravité OFF"
	gravity_status_label.add_theme_font_override("font", instruction_font)
	gravity_status_label.add_theme_color_override("font_color", accent)
	gravity_status_label.add_theme_color_override("font_shadow_color", Color(accent.r, accent.g, accent.b, 0.42))
	gravity_status_label.add_theme_constant_override("shadow_outline_size", 2)
	gravity_status_label.add_theme_constant_override("shadow_offset_x", 0)
	gravity_status_label.add_theme_constant_override("shadow_offset_y", 0)


func _apply_instruction_style(accent: Color) -> void:
	popup_style.border_color = accent
	popup_style.shadow_color = Color(accent.r, accent.g, accent.b, 0.38)
	for child in instruction_container.get_children():
		if child is BoxContainer:
			var label := child.get_node_or_null("Label") as Label
			if label != null:
				label.add_theme_font_override("font", instruction_font)
				label.add_theme_color_override("font_color", accent)
				label.add_theme_color_override("font_shadow_color", Color(accent.r, accent.g, accent.b, 0.42))
				label.add_theme_constant_override("outline_size", 0)
				label.add_theme_constant_override("shadow_outline_size", 3)
				label.add_theme_constant_override("shadow_offset_x", 0)
				label.add_theme_constant_override("shadow_offset_y", 0)


func _next_accent_color() -> Color:
	var accent_index := rng.randi_range(0, accent_colors.size() - 1)
	if accent_colors.size() > 1:
		while accent_index == last_accent_index:
			accent_index = rng.randi_range(0, accent_colors.size() - 1)
	last_accent_index = accent_index
	return accent_colors[accent_index]


func show_next_instruction() -> void:
	if hide_egg_timer.time_left > 0.0:
		return

	var cycle_instructions: Array[BoxContainer] = []

	for child in instruction_container.get_children():
		if child is BoxContainer:
			cycle_instructions.append(child)

	# Instructions are a deliberate, readable cycle. Random blanks and Easter Eggs
	# made the hint panel look broken rather than paced.
	if cycle_instructions.is_empty():
		return

	hide_egg_timer.stop()

	next_regular_instruction_index %= cycle_instructions.size()
	var next_instruction := cycle_instructions[next_regular_instruction_index]
	next_regular_instruction_index = (next_regular_instruction_index + 1) % cycle_instructions.size()
	if current_instruction == null:
		_activate_instruction(next_instruction, cycle_instructions)
		show_regular_popup()
	else:
		transition_to_instruction(next_instruction, cycle_instructions)


func _activate_instruction(next_instruction: BoxContainer, cycle_instructions: Array[BoxContainer]) -> void:
	for instruction in cycle_instructions:
		instruction.visible = false
	next_instruction.visible = true
	current_instruction = next_instruction
	last_instruction = current_instruction
	_apply_instruction_style(_next_accent_color())


func transition_to_instruction(next_instruction: BoxContainer, cycle_instructions: Array[BoxContainer]) -> void:
	if popup_tween != null and popup_tween.is_valid():
		popup_tween.kill()

	popup_tween = create_tween()
	popup_tween.tween_property(instruction_container, "modulate:a", 0.0, popup_transition_duration)

	popup_tween.tween_callback(func() -> void:
		_activate_instruction(next_instruction, cycle_instructions)
	)

	popup_tween.tween_property(instruction_container, "modulate:a", 1.0, popup_transition_duration)


func show_regular_popup() -> void:
	instruction_container.visible = true
	instruction_container.modulate.a = 0.0
	instruction_container.position = popup_base_position
	instruction_container.scale = Vector2(0.96, 0.96)

	popup_tween = create_tween()
	popup_tween.set_parallel(true)

	popup_tween.tween_property(
		instruction_container,
		"modulate:a",
		1.0,
		popup_fade_duration
	).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)

	popup_tween.tween_property(
		instruction_container,
		"scale",
		Vector2.ONE,
		popup_scale_duration
	).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)


func show_easter_egg_popup() -> void:
	instruction_container.visible = true
	instruction_container.modulate.a = 0.0
	instruction_container.scale = Vector2(0.8, 0.8)

	var tween := create_tween()
	tween.set_parallel(true)

	tween.tween_property(
		instruction_container,
		"modulate:a",
		1.0,
		0.12
	).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)

	tween.tween_property(
		instruction_container,
		"scale",
		Vector2.ONE,
		0.18
	).set_trans(Tween.TRANS_BACK).set_ease(Tween.EASE_OUT)


func hide_popup() -> void:
	var tween := create_tween()
	tween.set_parallel(false)

	tween.tween_property(
		instruction_container,
		"modulate:a",
		0.0,
		0.18
	).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN)

	tween.tween_callback(
		func() -> void:
			instruction_container.visible = false
	)


func _hide_easter_egg() -> void:
	if current_instruction != null:
		current_instruction.visible = false

	var tween := create_tween()
	tween.set_parallel(false)

	tween.tween_property(
		instruction_container,
		"modulate:a",
		0.0,
		0.12
	).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_IN)

	tween.tween_callback(
		func() -> void:
			instruction_container.visible = false
			current_instruction = null

			# Resume the regular instruction cycle after the Easter Egg disappears.
			show_next_instruction()
	)

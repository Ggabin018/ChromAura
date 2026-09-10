extends CanvasLayer

@export var change_interval: float = 3.0

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
@export var popup_background_color := Color(0.015, 0.025, 0.07, 0.68)
@export var popup_border_color := Color(0.25, 0.85, 1.0, 0.45)
@export var popup_border_width: int = 1
@export var popup_corner_radius: int = 16

@onready var instruction_container: Control = $DrawInstruction

var rng := RandomNumberGenerator.new()
var hide_egg_timer := Timer.new()

var last_instruction: BoxContainer = null
var current_instruction: BoxContainer = null


func _ready() -> void:
	rng.randomize()

	_setup_popup_style()

	for child in instruction_container.get_children():
		if child is BoxContainer:
			child.visible = false

	instruction_container.visible = false
	instruction_container.modulate.a = 0.0
	instruction_container.scale = Vector2(0.96, 0.96)

	hide_egg_timer.one_shot = true
	hide_egg_timer.timeout.connect(_hide_easter_egg)
	add_child(hide_egg_timer)

	show_random_instruction()

	var timer := Timer.new()
	timer.wait_time = change_interval
	timer.one_shot = false
	timer.autostart = true
	timer.timeout.connect(show_random_instruction)

	add_child(timer)


func _setup_popup_style() -> void:
	var style_box := StyleBoxFlat.new()

	style_box.bg_color = popup_background_color
	style_box.border_color = popup_border_color

	style_box.border_width_left = popup_border_width
	style_box.border_width_top = popup_border_width
	style_box.border_width_right = popup_border_width
	style_box.border_width_bottom = popup_border_width

	style_box.corner_radius_top_left = popup_corner_radius
	style_box.corner_radius_top_right = popup_corner_radius
	style_box.corner_radius_bottom_left = popup_corner_radius
	style_box.corner_radius_bottom_right = popup_corner_radius

	style_box.content_margin_left = 20.0
	style_box.content_margin_right = 20.0
	style_box.content_margin_top = 12.0
	style_box.content_margin_bottom = 12.0

	if instruction_container is PanelContainer:
		instruction_container.add_theme_stylebox_override(
			"panel",
			style_box
		)


func show_random_instruction() -> void:
	if hide_egg_timer.time_left > 0.0:
		return

	var regular_instructions: Array[BoxContainer] = []
	var easter_eggs: Array[BoxContainer] = []

	for child in instruction_container.get_children():
		if child is BoxContainer:
			if child.name.begins_with(easter_egg_prefix):
				easter_eggs.append(child)
			else:
				regular_instructions.append(child)

	# Safety check
	if regular_instructions.is_empty() and easter_eggs.is_empty():
		return

	hide_egg_timer.stop()

	# Hide all instructions
	for instruction in regular_instructions:
		instruction.visible = false

	for easter_egg in easter_eggs:
		easter_egg.visible = false

	current_instruction = null

	# Probability of showing nothing
	if rng.randf() < hide_probability:
		hide_popup()
		return

	var selected_list: Array[BoxContainer]
	var selected_easter_egg := false

	# Easter Eggs are selected less frequently
	if not easter_eggs.is_empty() and rng.randf() < easter_egg_probability:
		selected_list = easter_eggs
		selected_easter_egg = true
	else:
		selected_list = regular_instructions

	# Fallback if no regular instruction is available
	if selected_list.is_empty():
		selected_list = easter_eggs
		selected_easter_egg = true

	if selected_list.is_empty():
		hide_popup()
		return

	# Prevent immediate repetition
	var available_instructions: Array[BoxContainer] = []

	for instruction in selected_list:
		if instruction != last_instruction:
			available_instructions.append(instruction)

	# If there is only one candidate, allow it to be selected again
	if available_instructions.is_empty():
		available_instructions = selected_list

	var random_index := rng.randi_range(
		0,
		available_instructions.size() - 1
	)

	current_instruction = available_instructions[random_index]
	current_instruction.visible = true
	last_instruction = current_instruction

	if selected_easter_egg:
		show_easter_egg_popup()
		hide_egg_timer.start(easter_egg_duration)
	else:
		show_regular_popup()


func show_regular_popup() -> void:
	instruction_container.visible = true
	instruction_container.modulate.a = 0.0
	instruction_container.scale = Vector2(0.96, 0.96)

	var tween := create_tween()
	tween.set_parallel(true)

	tween.tween_property(
		instruction_container,
		"modulate:a",
		1.0,
		popup_fade_duration
	).set_trans(Tween.TRANS_SINE).set_ease(Tween.EASE_OUT)

	tween.tween_property(
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

			# Draw a new instruction after the Easter Egg disappears
			show_random_instruction()
	)

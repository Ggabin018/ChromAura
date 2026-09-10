class_name HeartParticleManager
extends Control

## Manages floating neon hearts spawned by heart hand gestures.
## Uses additive blending for vibrant neon glow effects.

@export var heart_texture: Texture2D = preload("res://assets/neon_heart.png")
@export_range(0.02, 0.50, 0.01) var spawn_interval: float = 0.11
@export_range(50.0, 600.0, 10.0) var min_speed: float = 160.0
@export_range(50.0, 800.0, 10.0) var max_speed: float = 290.0
@export_range(1.0, 6.0, 0.1) var min_lifetime: float = 2.4
@export_range(1.0, 8.0, 0.1) var max_lifetime: float = 3.8

class FloatingHeart:
	var position := Vector2.ZERO
	var base_x := 0.0
	var velocity_y := -200.0
	var current_scale := 0.0
	var target_scale := 0.5
	var rotation := 0.0
	var rotation_speed := 0.0
	var wobble_freq := 2.5
	var wobble_amp := 18.0
	var wobble_phase := 0.0
	var lifetime := 3.0
	var age := 0.0
	var alpha := 0.0
	var color_tint := Color.WHITE


class HeartSparkle:
	var position := Vector2.ZERO
	var velocity := Vector2.ZERO
	var lifetime := 0.8
	var age := 0.0
	var radius := 2.5
	var color := Color(0.4, 0.85, 1.0, 0.9)


var _active_anchors: Array[Dictionary] = []
var _hearts: Array[FloatingHeart] = []
var _sparkles: Array[HeartSparkle] = []
var _spawn_timer := 0.0
var _material_add: CanvasItemMaterial


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	_material_add = CanvasItemMaterial.new()
	_material_add.blend_mode = CanvasItemMaterial.BLEND_MODE_ADD
	material = _material_add


func update_heart_state(anchors: Array[Dictionary]) -> void:
	_active_anchors = anchors


func is_heart_active() -> bool:
	return not _active_anchors.is_empty()


func _process(delta: float) -> void:
	var vp_size := get_viewport_rect().size
	if vp_size.x <= 0 or vp_size.y <= 0:
		vp_size = Vector2(1280, 720)

	if not _active_anchors.is_empty():
		_spawn_timer += delta
		while _spawn_timer >= spawn_interval:
			_spawn_timer -= spawn_interval
			for item in _active_anchors:
				var anchor_uv: Vector2 = item.get("anchor", Vector2(0.5, 0.5))
				var scale_hint: float = item.get("scale_hint", 1.0)
				_spawn_heart(anchor_uv * vp_size, scale_hint)
	else:
		_spawn_timer = 0.0

	# Update existing floating hearts
	var alive_hearts: Array[FloatingHeart] = []
	for h in _hearts:
		h.age += delta
		if h.age >= h.lifetime:
			continue

		var progress := h.age / h.lifetime
		h.position.y += h.velocity_y * delta
		h.wobble_phase += h.wobble_freq * delta
		h.position.x = h.base_x + sin(h.wobble_phase) * h.wobble_amp
		h.rotation += h.rotation_speed * delta

		# Scale animation: quick pop-in then gentle floating pulse
		if progress < 0.12:
			h.current_scale = lerpf(0.12 * h.target_scale, h.target_scale, progress / 0.12)
		else:
			var pulse := 1.0 + 0.06 * sin(h.age * 5.0)
			h.current_scale = h.target_scale * pulse

		# Alpha animation: smooth fade in, stay luminous, then fade out
		if progress < 0.10:
			h.alpha = progress / 0.10
		elif progress > 0.65:
			h.alpha = 1.0 - ((progress - 0.65) / 0.35)
		else:
			h.alpha = 1.0

		alive_hearts.append(h)
	_hearts = alive_hearts

	# Update sparkles
	var alive_sparkles: Array[HeartSparkle] = []
	for s in _sparkles:
		s.age += delta
		if s.age >= s.lifetime:
			continue
		s.position += s.velocity * delta
		s.velocity *= 0.94
		alive_sparkles.append(s)
	_sparkles = alive_sparkles

	if not _hearts.is_empty() or not _sparkles.is_empty() or not _active_anchors.is_empty():
		queue_redraw()


func _spawn_heart(screen_pos: Vector2, scale_multiplier: float) -> void:
	if _hearts.size() >= 120:
		return

	var h := FloatingHeart.new()
	var jitter := Vector2(randf_range(-25.0, 25.0), randf_range(-15.0, 15.0))
	h.position = screen_pos + jitter
	h.base_x = h.position.x
	h.velocity_y = -randf_range(min_speed, max_speed)
	h.target_scale = randf_range(0.24, 0.42) * scale_multiplier
	h.current_scale = 0.1 * h.target_scale
	h.rotation = randf_range(-0.18, 0.18)
	h.rotation_speed = randf_range(-0.45, 0.45)
	h.wobble_freq = randf_range(1.8, 3.8)
	h.wobble_amp = randf_range(12.0, 28.0)
	h.wobble_phase = randf_range(0.0, TAU)
	h.lifetime = randf_range(min_lifetime, max_lifetime)
	h.age = 0.0
	h.alpha = 0.0

	# Random slight variation: cyan-weighted, magenta-weighted, or pure vibrant
	var color_mode := randi() % 3
	if color_mode == 0:
		h.color_tint = Color(0.9, 1.0, 1.0, 1.0)
	elif color_mode == 1:
		h.color_tint = Color(1.0, 0.9, 1.0, 1.0)
	else:
		h.color_tint = Color.WHITE

	_hearts.append(h)

	# Spawn accompanying sparkles
	var sparkle_count := randi_range(3, 5)
	for i in range(sparkle_count):
		if _sparkles.size() >= 150:
			break
		var s := HeartSparkle.new()
		s.position = h.position + Vector2(randf_range(-20.0, 20.0), randf_range(-20.0, 20.0))
		var angle := randf_range(0.0, TAU)
		var spd := randf_range(30.0, 120.0)
		s.velocity = Vector2(cos(angle), sin(angle)) * spd + Vector2(0.0, -40.0)
		s.lifetime = randf_range(0.4, 0.9)
		s.age = 0.0
		s.radius = randf_range(1.8, 3.8)
		if randf() < 0.5:
			s.color = Color(0.3, 0.9, 1.0, 0.85)
		else:
			s.color = Color(1.0, 0.35, 0.9, 0.85)
		_sparkles.append(s)


func _draw() -> void:
	if heart_texture == null:
		return

	var tex_size := heart_texture.get_size()
	var origin_offset := -tex_size * 0.5

	# Draw floating hearts
	for h in _hearts:
		if h.alpha <= 0.001 or h.current_scale <= 0.001:
			continue

		var tint := h.color_tint
		tint.a = h.alpha

		var xform := Transform2D().translated(h.position).rotated(h.rotation).scaled(Vector2.ONE * h.current_scale)
		draw_set_transform_matrix(xform)
		draw_texture(heart_texture, origin_offset, tint)

	# Reset transform for sparkles
	draw_set_transform_matrix(Transform2D.IDENTITY)

	# Draw sparkles
	for s in _sparkles:
		var progress := s.age / s.lifetime
		var alpha := 1.0 - progress
		var col := s.color
		col.a *= alpha
		draw_circle(s.position, s.radius * (1.0 - 0.5 * progress), col)

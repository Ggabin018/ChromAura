class_name HeartParticleManager
extends Control

## Manages glowing particle hearts that float across the screen when heart gestures are made.
## Each heart is formed by a constellation of sparkling particle stars (cyan & magenta).
## Uses additive blending for vibrant luminescent aura effects.

@export_range(0.02, 0.40, 0.01) var spawn_interval: float = 0.08
@export_range(60.0, 500.0, 10.0) var min_speed: float = 140.0
@export_range(80.0, 700.0, 10.0) var max_speed: float = 270.0
@export_range(1.0, 6.0, 0.1) var min_lifetime: float = 2.6
@export_range(1.0, 8.0, 0.1) var max_lifetime: float = 4.2

const HEART_POINT_COUNT := 28

class FloatingParticleHeart:
	var position := Vector2.ZERO
	var base_x := 0.0
	var velocity_y := -200.0
	var current_radius := 0.0
	var target_radius := 40.0
	var sparkle_scale := 0.55
	var rotation := 0.0
	var rotation_speed := 0.0
	var wobble_freq := 2.5
	var wobble_amp := 20.0
	var wobble_phase := 0.0
	var lifetime := 3.2
	var age := 0.0
	var alpha := 0.0


class HeartSparkle:
	var position := Vector2.ZERO
	var velocity := Vector2.ZERO
	var lifetime := 1.2
	var age := 0.0
	var scale := 0.45
	var color := Color(0.3, 0.88, 1.0, 0.9)


var _active_anchors: Array[Dictionary] = []
var _hearts: Array[FloatingParticleHeart] = []
var _sparkles: Array[HeartSparkle] = []
var _spawn_timer := 0.0
var _material_add: CanvasItemMaterial
var _sparkle_texture: ImageTexture

var _heart_points: Array[Vector2] = []
var _heart_point_colors: Array[Color] = []


func _ready() -> void:
	mouse_filter = Control.MOUSE_FILTER_IGNORE
	_material_add = CanvasItemMaterial.new()
	_material_add.blend_mode = CanvasItemMaterial.BLEND_MODE_ADD
	material = _material_add

	_sparkle_texture = _create_sparkle_texture(24)
	_init_heart_points()


func update_heart_state(anchors: Array[Dictionary]) -> void:
	_active_anchors = anchors


func is_heart_active() -> bool:
	return not _active_anchors.is_empty()


func _init_heart_points() -> void:
	_heart_points.clear()
	_heart_point_colors.clear()
	for i in range(HEART_POINT_COUNT):
		var t := float(i) / float(HEART_POINT_COUNT) * TAU
		# Standard parametric heart formula: x = 16*sin^3(t), y = -(13*cos(t) - 5*cos(2t) - 2*cos(3t) - cos(4t))
		var hx := 16.0 * pow(sin(t), 3)
		var hy := -(13.0 * cos(t) - 5.0 * cos(2.0 * t) - 2.0 * cos(3.0 * t) - cos(4.0 * t))
		var pt := Vector2(hx / 16.0, (hy - 1.0) / 16.0)
		_heart_points.append(pt)

		# Left lobe = electric cyan, right lobe = neon magenta, apex/top center = pure bright white
		var col: Color
		if pt.x < -0.15:
			col = Color(0.25, 0.90, 1.0, 1.0) # Cyan
		elif pt.x > 0.15:
			col = Color(1.0, 0.28, 0.90, 1.0) # Magenta
		else:
			col = Color(0.92, 0.95, 1.0, 1.0) # Center bright glow
		_heart_point_colors.append(col)


func _create_sparkle_texture(size: int = 24) -> ImageTexture:
	var image := Image.create_empty(size, size, false, Image.FORMAT_RGBA8)
	var center := float(size - 1) * 0.5
	for y in range(size):
		for x in range(size):
			var offset := Vector2(float(x) - center, float(y) - center)
			var dist := offset.length() / center
			if dist >= 1.0:
				image.set_pixel(x, y, Color(1, 1, 1, 0))
				continue
			var nx := absf(offset.x) / center
			var ny := absf(offset.y) / center
			var core := pow(clampf(1.0 - dist * 2.2, 0.0, 1.0), 2.0)
			var h_ray := pow(clampf(1.0 - nx, 0.0, 1.0), 1.2) * pow(clampf(1.0 - ny * 3.5, 0.0, 1.0), 2.0)
			var v_ray := pow(clampf(1.0 - ny, 0.0, 1.0), 1.2) * pow(clampf(1.0 - nx * 3.5, 0.0, 1.0), 2.0)
			var a := clampf(core * 1.3 + h_ray * 0.7 + v_ray * 0.7, 0.0, 1.0)
			image.set_pixel(x, y, Color(1, 1, 1, a))
	return ImageTexture.create_from_image(image)


func _process(delta: float) -> void:
	var vp_size := get_viewport_rect().size
	if vp_size.x <= 0 or vp_size.y <= 0:
		vp_size = Vector2(1280, 720)

	if not _active_anchors.is_empty():
		_spawn_timer += delta
		while _spawn_timer >= spawn_interval:
			_spawn_timer -= spawn_interval

			# 1. Spawn at hand anchor(s)
			for item in _active_anchors:
				var anchor_uv: Vector2 = item.get("anchor", Vector2(0.5, 0.5))
				var scale_hint: float = item.get("scale_hint", 1.0)
				var anchor_pos := Vector2(anchor_uv.x * vp_size.x, anchor_uv.y * vp_size.y)
				_spawn_heart(anchor_pos, randf_range(34.0, 58.0) * scale_hint)

			# 2. Spawn multiple particle hearts across the ENTIRE SCREEN
			var screen_spawns := randi_range(2, 3)
			for k in range(screen_spawns):
				var screen_x := randf_range(0.04, 0.96) * vp_size.x
				var screen_y := randf_range(vp_size.y * 0.60, vp_size.y * 1.02)
				var radius := randf_range(22.0, 52.0)
				_spawn_heart(Vector2(screen_x, screen_y), radius)

			# 3. Ambient stardust sparkles across the screen
			var extra_sparkles := randi_range(3, 5)
			for s_idx in range(extra_sparkles):
				var sx := randf_range(0.02, 0.98) * vp_size.x
				var sy := randf_range(vp_size.y * 0.65, vp_size.y * 1.02)
				_spawn_sparkle(Vector2(sx, sy), Vector2(randf_range(-25.0, 25.0), -randf_range(70.0, 190.0)))
	else:
		_spawn_timer = 0.0

	# Update hearts
	var alive_hearts: Array[FloatingParticleHeart] = []
	for h in _hearts:
		h.age += delta
		if h.age >= h.lifetime:
			continue

		var progress := h.age / h.lifetime
		h.position.y += h.velocity_y * delta
		h.wobble_phase += h.wobble_freq * delta
		h.position.x = h.base_x + sin(h.wobble_phase) * h.wobble_amp
		h.rotation += h.rotation_speed * delta

		# Radius animation: expand into view, then gentle breathing pulse
		if progress < 0.12:
			h.current_radius = lerpf(0.12 * h.target_radius, h.target_radius, progress / 0.12)
		else:
			var breathe := 1.0 + 0.05 * sin(h.age * 5.0)
			h.current_radius = h.target_radius * breathe

		# Alpha animation: smooth fade in, luminous duration, then smooth fade out
		if progress < 0.10:
			h.alpha = progress / 0.10
		elif progress > 0.60:
			h.alpha = 1.0 - ((progress - 0.60) / 0.40)
		else:
			h.alpha = 1.0

		# Sparkle wake trailing behind heart
		if randf() < 0.30:
			var trail_pos := h.position + Vector2(randf_range(-h.current_radius * 0.4, h.current_radius * 0.4), h.current_radius * 0.7)
			_spawn_sparkle(trail_pos, Vector2(randf_range(-18.0, 18.0), randf_range(10.0, 35.0)))

		alive_hearts.append(h)
	_hearts = alive_hearts

	# Update sparkles
	var alive_sparkles: Array[HeartSparkle] = []
	for s in _sparkles:
		s.age += delta
		if s.age >= s.lifetime:
			continue
		s.position += s.velocity * delta
		s.velocity.x += sin(s.age * 4.0) * 12.0 * delta
		s.velocity.y *= 0.985
		alive_sparkles.append(s)
	_sparkles = alive_sparkles

	if not _hearts.is_empty() or not _sparkles.is_empty() or not _active_anchors.is_empty():
		queue_redraw()


func _spawn_heart(origin_pos: Vector2, radius: float) -> void:
	if _hearts.size() >= 130:
		return

	var h := FloatingParticleHeart.new()
	var jitter := Vector2(randf_range(-25.0, 25.0), randf_range(-15.0, 15.0))
	h.position = origin_pos + jitter
	h.base_x = h.position.x
	h.velocity_y = -randf_range(min_speed, max_speed)
	h.target_radius = radius
	h.current_radius = 0.15 * radius
	h.sparkle_scale = clampf(radius / 40.0 * 0.55, 0.35, 0.85)
	h.rotation = randf_range(-0.16, 0.16)
	h.rotation_speed = randf_range(-0.35, 0.35)
	h.wobble_freq = randf_range(1.6, 3.2)
	h.wobble_amp = randf_range(12.0, 32.0)
	h.wobble_phase = randf_range(0.0, TAU)
	h.lifetime = randf_range(min_lifetime, max_lifetime)
	h.age = 0.0
	h.alpha = 0.0

	_hearts.append(h)


func _spawn_sparkle(pos: Vector2, vel: Vector2) -> void:
	if _sparkles.size() >= 160:
		return

	var s := HeartSparkle.new()
	s.position = pos
	s.velocity = vel
	s.lifetime = randf_range(0.7, 1.4)
	s.age = 0.0
	s.scale = randf_range(0.28, 0.55)
	if randf() < 0.5:
		s.color = Color(0.28, 0.90, 1.0, 0.95) # Cyan
	else:
		s.color = Color(1.0, 0.30, 0.88, 0.95) # Magenta
	_sparkles.append(s)


func _draw() -> void:
	if _sparkle_texture == null:
		return

	var spark_size := _sparkle_texture.get_size()

	# Draw all floating particle hearts
	for h in _hearts:
		if h.alpha <= 0.001 or h.current_radius <= 0.5:
			continue

		var cos_r := cos(h.rotation)
		var sin_r := sin(h.rotation)
		var rad := h.current_radius

		# Draw the glittering particles defining the heart shape
		for i in range(HEART_POINT_COUNT):
			var local_pt: Vector2 = _heart_points[i] * rad
			var rx := local_pt.x * cos_r - local_pt.y * sin_r
			var ry := local_pt.x * sin_r + local_pt.y * cos_r
			var pt_world := h.position + Vector2(rx, ry)

			var twinkle := 0.72 + 0.38 * sin(h.age * 9.0 + float(i) * 1.3)
			var col: Color = _heart_point_colors[i]
			col.a = clampf(h.alpha * twinkle, 0.0, 1.0)

			var draw_size := spark_size * (h.sparkle_scale * twinkle)
			draw_texture_rect(
				_sparkle_texture,
				Rect2(pt_world - draw_size * 0.5, draw_size),
				false,
				col
			)

		# Center glowing nucleus
		var core_col := Color(0.92, 0.75, 1.0, h.alpha * 0.40)
		var core_draw_size := spark_size * (h.sparkle_scale * 1.1)
		draw_texture_rect(
			_sparkle_texture,
			Rect2(h.position - core_draw_size * 0.5, core_draw_size),
			false,
			core_col
		)

	# Draw loose stardust sparkles
	for s in _sparkles:
		var progress := s.age / s.lifetime
		var alpha := 1.0 - progress
		var col := s.color
		col.a *= alpha
		var draw_size := spark_size * (s.scale * alpha)
		draw_texture_rect(
			_sparkle_texture,
			Rect2(s.position - draw_size * 0.5, draw_size),
			false,
			col
		)

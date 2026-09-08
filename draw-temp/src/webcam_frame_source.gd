class_name WebcamFrameSource
extends FrameSource

const DEFAULT_FRAME_SIZE := Vector2i(640, 480)

@export var target_size := Vector2i(1280, 720)
@export_range(1.0, 240.0, 1.0) var target_fps := 30.0

@onready var _viewport: SubViewport = $Viewport
@onready var _camera_image: TextureRect = $Viewport/CameraImage

var _camera_feed: CameraFeed
var _camera_format: Dictionary = {}
var _source_texture: CameraTexture
var _running := false


func _exit_tree() -> void:
	stop()


func start() -> void:
	if _running:
		return

	_running = true
	CameraServer.camera_feed_added.connect(_on_camera_feed_added)
	CameraServer.camera_feed_removed.connect(_on_camera_feed_removed)
	CameraServer.monitoring_feeds = true
	_start_first_camera()


func stop() -> void:
	if not _running:
		return

	_running = false
	_disconnect_camera()
	if CameraServer.camera_feed_added.is_connected(_on_camera_feed_added):
		CameraServer.camera_feed_added.disconnect(_on_camera_feed_added)
	if CameraServer.camera_feed_removed.is_connected(_on_camera_feed_removed):
		CameraServer.camera_feed_removed.disconnect(_on_camera_feed_removed)
	CameraServer.monitoring_feeds = false


func get_output_texture() -> Texture2D:
	return _viewport.get_texture()


func get_frame_image() -> Image:
	if _camera_feed == null:
		return null
	return _viewport.get_texture().get_image()


func get_frame_size() -> Vector2i:
	return _viewport.size


func _start_first_camera() -> void:
	if not _running or _camera_feed != null:
		return

	var feeds := CameraServer.feeds()
	if feeds.is_empty():
		status_changed.emit("Waiting for a camera...")
		return

	_camera_feed = feeds[0]
	var formats := _camera_feed.get_formats()
	if not formats.is_empty():
		var format_index := _choose_camera_format(formats)
		_camera_format = formats[format_index]
		if not _camera_feed.set_format(format_index, {}):
			status_changed.emit("Failed to activate the camera format.")
			_camera_feed = null
			return

	_camera_feed.format_changed.connect(_configure_camera_texture)
	_camera_feed.frame_changed.connect(_on_camera_frame_changed)
	_camera_feed.feed_is_active = true
	_configure_camera_texture()
	status_changed.emit("Camera: %s. Detecting hands..." % _camera_feed.get_name())


func _disconnect_camera() -> void:
	if _camera_feed == null:
		return

	_camera_feed.feed_is_active = false
	if _camera_feed.format_changed.is_connected(_configure_camera_texture):
		_camera_feed.format_changed.disconnect(_configure_camera_texture)
	if _camera_feed.frame_changed.is_connected(_on_camera_frame_changed):
		_camera_feed.frame_changed.disconnect(_on_camera_frame_changed)

	_camera_feed = null
	_camera_format = {}
	_source_texture = null
	_camera_image.texture = null
	_camera_image.material = null
	_apply_frame_size(DEFAULT_FRAME_SIZE, true)


func _choose_camera_format(formats: Array) -> int:
	var best_index := 0
	var best_score := -INF

	for index in range(formats.size()):
		var format: Dictionary = formats[index]
		var size := Vector2i(
			int(format.get("width", 0)),
			int(format.get("height", 0)),
		)
		var resolution_penalty := absi(size.x - target_size.x) \
			+ absi(size.y - target_size.y)
		var score := minf(_get_format_fps(format), target_fps) * 10000.0 \
			- float(resolution_penalty)
		if score > best_score:
			best_score = score
			best_index = index

	return best_index


func _get_format_fps(format: Dictionary) -> float:
	if format.has("fps"):
		return float(format["fps"])

	var numerator := int(format.get("frame_numerator", 0))
	if numerator != 0:
		return float(format.get("frame_denominator", 0)) / float(numerator)

	numerator = int(format.get("framerate_numerator", 0))
	if numerator != 0:
		return float(format.get("framerate_denominator", 0)) / float(numerator)

	return target_fps


func _configure_camera_texture() -> void:
	if _camera_feed == null:
		return

	_source_texture = null
	match _camera_feed.get_datatype():
		CameraFeed.FEED_RGB:
			_source_texture = _create_camera_texture(CameraServer.FEED_RGBA_IMAGE)
			_camera_image.material = null
			_camera_image.texture = _source_texture

		CameraFeed.FEED_YCBCR:
			_source_texture = _create_camera_texture(CameraServer.FEED_YCBCR_IMAGE)
			var material := ShaderMaterial.new()
			material.shader = load("res://src/yuy2_to_rgb.gdshader")
			material.set_shader_parameter("texture_yuy2", _source_texture)
			_camera_image.material = material
			_camera_image.texture = null

		CameraFeed.FEED_YCBCR_SEP:
			_source_texture = _create_camera_texture(CameraServer.FEED_Y_IMAGE)
			var material := ShaderMaterial.new()
			material.shader = load("res://src/yuv420_to_rgb.gdshader")
			material.set_shader_parameter("texture_y", _source_texture)
			material.set_shader_parameter(
				"texture_uv",
				_create_camera_texture(CameraServer.FEED_CBCR_IMAGE),
			)
			_camera_image.material = material
			_camera_image.texture = null

		_:
			status_changed.emit("Unsupported camera color format.")
			return

	_apply_frame_size(_get_camera_texture_size(), true)


func _get_camera_texture_size() -> Vector2i:
	if _source_texture != null:
		var texture_size := Vector2i(_source_texture.get_size())
		if texture_size.x > 1 and texture_size.y > 1:
			return texture_size

	return Vector2i(
		maxi(int(_camera_format.get("width", DEFAULT_FRAME_SIZE.x)), 1),
		maxi(int(_camera_format.get("height", DEFAULT_FRAME_SIZE.y)), 1),
	)


func _apply_frame_size(frame_size: Vector2i, force: bool = false) -> void:
	if not force and _viewport.size == frame_size:
		return

	_viewport.size = frame_size
	_camera_image.size = Vector2(frame_size)
	if _camera_image.material != null:
		_camera_image.texture = _create_blank_texture(frame_size)
	frame_size_changed.emit(frame_size)


func _create_camera_texture(which_feed: CameraServer.FeedImage) -> CameraTexture:
	var texture := CameraTexture.new()
	texture.camera_feed_id = _camera_feed.get_id()
	texture.which_feed = which_feed
	return texture


func _create_blank_texture(frame_size: Vector2i) -> ImageTexture:
	var image := Image.create_empty(frame_size.x, frame_size.y, false, Image.FORMAT_RGB8)
	image.fill(Color.BLACK)
	return ImageTexture.create_from_image(image)


func _on_camera_frame_changed() -> void:
	_apply_frame_size(_get_camera_texture_size())
	frame_available.emit()


func _on_camera_feed_added(_id: int) -> void:
	_start_first_camera.call_deferred()


func _on_camera_feed_removed(id: int) -> void:
	if _camera_feed == null or _camera_feed.get_id() != id:
		return

	_disconnect_camera()
	status_changed.emit("Camera disconnected. Waiting for a camera...")
	_start_first_camera.call_deferred()

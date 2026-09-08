extends Control

@onready var depth_camera: DepthCameraNode = $DepthCameraNode

func _ready():
	# Connect to frame signals
	depth_camera.rgb_frame_ready.connect(_on_rgb_frame)
	depth_camera.depth_frame_ready.connect(_on_depth_frame)
	
	# Optional: Configure settings
	depth_camera.min_depth = 0.5   # meters
	depth_camera.max_depth = 3.0   # meters
	depth_camera.auto_reconnect = true
	
	# Start streaming (or enable auto_start in inspector)
	depth_camera.start_streaming()
	print("Camera is READY")
	
func get_depth_color(col_min: Color, col_max: Color, depth: float) -> Color:
	var t = clamp(depth, 0.0, 1.0)
	return col_max.lerp(col_min, t)

func _on_rgb_frame(image: ImageTexture):
	$RGBTexture.texture = image

func _on_depth_frame(image: ImageTexture):
	$DepthTexture.texture = image
	
	var w = image.get_width()
	var h = image.get_height()
	
	var img: Image = Image.create(image.get_width(), image.get_height(), false, Image.FORMAT_RGB8)
	
	var tmp = image.get_image()
	
	var threshold = 0.65
	
	# 2. Iterate through every pixel
	for y in range(h):
		for x in range(w):
			var input_color: Color = tmp.get_pixel(x, y)
	
			# Check your condition (e.g., Red channel value)
			if input_color.r < threshold:
				img.set_pixel(w - x, y, Color(0, 0, 0, 1))
			else: 
				var normalized = inverse_lerp(0.65, 1.0, input_color.r)
				var output_color = get_depth_color(
					Color8(180, 250, 255),
					Color8(110, 20, 220),
					normalized
				)
				img.set_pixel(w - x, y, output_color)
	
	$OutTexture.texture = ImageTexture.create_from_image(img)

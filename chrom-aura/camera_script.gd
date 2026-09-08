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

func _on_rgb_frame(image: ImageTexture):
	$RGBTexture.texture = image

func _on_depth_frame(image: ImageTexture):
	$DepthTexture.texture = image
	
	var img: Image = Image.create(image.get_width(), image.get_height(), false, Image.FORMAT_RGB8)
	
	var tmp = image.get_image()
	
	var threshold = 0.65
	
	# 2. Iterate through every pixel
	for y in range(img.get_height()):
		for x in range(img.get_width()):
			var pixel_color: Color = tmp.get_pixel(x, y)
			
			# Check your condition (e.g., Red channel value)
			if pixel_color.r < threshold:
				# Erase the pixel by changing it to full transparency
				img.set_pixel(x, y, Color(0, 0, 0, 1))
			else:
				img.set_pixel(x, y, 
				Color.from_hsv((0.1 + (pixel_color.r * 8) / threshold), 1, 1))
				
	$OutTexture.texture = ImageTexture.create_from_image(img)

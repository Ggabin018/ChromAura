class_name FrameSource
extends Node

signal frame_available
signal frame_size_changed(frame_size: Vector2i)
signal status_changed(message: String)


func start() -> void:
	pass


func stop() -> void:
	pass


func get_output_texture() -> Texture2D:
	return null


func get_frame_image() -> Image:
	return null


func get_frame_size() -> Vector2i:
	return Vector2i.ZERO

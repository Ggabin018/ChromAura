class_name HandOverlay
extends Control

const HAND_CONNECTIONS := [
	0, 1, 1, 2, 2, 3, 3, 4,
	0, 5, 5, 6, 6, 7, 7, 8,
	5, 9, 9, 10, 10, 11, 11, 12,
	9, 13, 13, 14, 14, 15, 15, 16,
	13, 17, 0, 17, 17, 18, 18, 19, 19, 20,
]
const LANDMARK_COLOR := Color(1.0, 0.2, 0.35)
const CONNECTION_COLOR := Color(0.1, 1.0, 0.45)
const LANDMARK_RADIUS := 5.0
const CONNECTION_WIDTH := 3.0

var _hands: Array[PackedVector2Array] = []
var _source_size := Vector2i(640, 480)


func show_hands(hands: Array[PackedVector2Array], source_size: Vector2i) -> void:
	_hands = hands
	_source_size = source_size
	queue_redraw()


func _notification(what: int) -> void:
	if what == NOTIFICATION_RESIZED:
		queue_redraw()


func _draw() -> void:
	if _source_size.x <= 0 or _source_size.y <= 0:
		return

	var target := _content_rect()
	for hand in _hands:
		for connection_index in range(0, HAND_CONNECTIONS.size(), 2):
			var from_index: int = HAND_CONNECTIONS[connection_index]
			var to_index: int = HAND_CONNECTIONS[connection_index + 1]
			if from_index < hand.size() and to_index < hand.size():
				draw_line(
					_to_screen(hand[from_index], target),
					_to_screen(hand[to_index], target),
					CONNECTION_COLOR,
					CONNECTION_WIDTH,
					true,
				)

		for landmark in hand:
			draw_circle(_to_screen(landmark, target), LANDMARK_RADIUS, LANDMARK_COLOR)


func _content_rect() -> Rect2:
	var source_aspect := float(_source_size.x) / float(_source_size.y)
	var control_aspect := size.x / maxf(size.y, 1.0)
	var rendered_size := size
	var origin := Vector2.ZERO

	if control_aspect > source_aspect:
		rendered_size.x = size.y * source_aspect
		origin.x = (size.x - rendered_size.x) * 0.5
	else:
		rendered_size.y = size.x / source_aspect
		origin.y = (size.y - rendered_size.y) * 0.5

	return Rect2(origin, rendered_size)


func _to_screen(normalized_point: Vector2, target: Rect2) -> Vector2:
	return target.position + normalized_point * target.size

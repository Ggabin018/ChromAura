class_name HandObservation
extends RefCounted

## Raw hand data extracted from a single MediaPipe result.

var landmarks_2d := PackedVector2Array()
var landmarks_3d := PackedVector3Array()
var handedness: StringName = HandPose.UNKNOWN_HAND
var handedness_score := 0.0


func is_valid() -> bool:
	return (
		landmarks_2d.size() == HandPose.LANDMARK_COUNT
		and landmarks_3d.size() == HandPose.LANDMARK_COUNT
	)

extends SceneTree

var _failures: Array[String] = []


func _init() -> void:
	call_deferred("_run")


func _run() -> void:
	var audio_mgr_script: Script = load("res://audio_manager.gd")
	var audio_mgr: Node = audio_mgr_script.new()
	root.add_child(audio_mgr)

	if audio_mgr._wilhelm_player == null:
		_failures.append("Wilhelm player was not created")

	if audio_mgr._wilhelm_stream == null:
		_failures.append("Wilhelm audio stream failed to load from %s" % audio_mgr.wilhelm_track_path)
	else:
		print("Wilhelm audio stream loaded successfully (length: %.2fs)" % audio_mgr._wilhelm_stream.get_length())

	var time_before: int = audio_mgr._last_wilhelm_time_ms
	audio_mgr.play_wilhelm_scream()
	if audio_mgr._last_wilhelm_time_ms <= time_before:
		_failures.append("play_wilhelm_scream did not update last_wilhelm_time_ms")

	var last_time: int = audio_mgr._last_wilhelm_time_ms
	# Immediate second play should be blocked by 2.0s cooldown
	audio_mgr.play_wilhelm_scream()
	if audio_mgr._last_wilhelm_time_ms != last_time:
		_failures.append("Cooldown failed to prevent immediate re-trigger of Wilhelm scream")

	audio_mgr.free()

	if _failures.is_empty():
		print("AudioEasterEgg tests: PASS")
		quit(0)
	else:
		for failure in _failures:
			push_error(failure)
		print("AudioEasterEgg tests: FAIL (%d)" % _failures.size())
		quit(1)

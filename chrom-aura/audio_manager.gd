class_name AudioManager
extends Node

## AudioManager for ChromAura
## Manages continuous alternating ambient music (crossfade)
## and reactive glitter sound effect when user points up and draws.

@export_group("Pistes Audio")
@export var ambient_track_paths: Array[String] = [
	"res://assets/audio/ambient_1.mp3",
	"res://assets/audio/ambient_2.mp3"
]
@export var glitter_track_path: String = "res://assets/audio/glitter.mp3"

@export_group("Volumes (dB)")
@export_range(-80.0, 6.0, 0.5) var ambient_volume_db: float = -8.0
@export_range(-80.0, 6.0, 0.5) var glitter_volume_db: float = -2.0

@export_group("Transitions")
## Durée du crossfade entre deux musiques d'ambiance (en secondes)
@export var ambient_crossfade_time: float = 6.0
## Si > 0, alterne après cette durée au lieu d'attendre la fin de la piste (pour tests/rotation rapide)
@export var ambient_duration_override: float = 0.0
## Durée du fondu d'entrée lorsque l'utilisateur commence à dessiner
@export var glitter_fade_in_time: float = 0.35
## Durée du fondu de sortie lorsque l'utilisateur cesse de dessiner
@export var glitter_fade_out_time: float = 0.70

var _ambient_players: Array[AudioStreamPlayer] = []
var _ambient_streams: Array[AudioStream] = []
var _active_ambient_index: int = 0
var _active_player_slot: int = 0

var _glitter_player: AudioStreamPlayer
var _glitter_stream: AudioStream

var _ambient_tween: Tween
var _glitter_tween: Tween
var _is_drawing: bool = false
var _crossfading: bool = false
var _last_pointing_time_ms: int = 0
var _stop_drawing_time_ms: int = 0

var _shot_players: Array[AudioStreamPlayer] = []
var _shot_stream: AudioStreamWAV = null
var _shot_player_index: int = 0


func _ready() -> void:
	_setup_audio_players()
	_load_audio_resources()
	start_ambient()


func _setup_audio_players() -> void:
	for i in range(2):
		var p := AudioStreamPlayer.new()
		p.name = "AmbientPlayer%d" % i
		p.bus = "Master"
		p.volume_db = -80.0
		add_child(p)
		_ambient_players.append(p)
		var slot := i
		p.finished.connect(func(): _on_player_finished(slot))

	_glitter_player = AudioStreamPlayer.new()
	_glitter_player.name = "GlitterPlayer"
	_glitter_player.bus = "Master"
	_glitter_player.volume_db = -80.0
	add_child(_glitter_player)

	_shot_stream = _create_shot_stream()
	for i in range(4):
		var sp := AudioStreamPlayer.new()
		sp.name = "ShotPlayer%d" % i
		sp.bus = "Master"
		sp.volume_db = -2.0
		sp.stream = _shot_stream
		add_child(sp)
		_shot_players.append(sp)


func _create_shot_stream() -> AudioStreamWAV:
	var sample_rate := 22050
	var duration := 0.22
	var total_samples := int(sample_rate * duration)
	var data := PackedByteArray()
	data.resize(total_samples)
	var phase := 0.0
	for i in range(total_samples):
		var t := float(i) / float(total_samples)
		var freq := lerpf(1250.0, 160.0, t * t)
		phase += freq / float(sample_rate) * TAU
		var env := exp(-8.5 * t)
		var sample := (sin(phase) + 0.35 * sin(phase * 2.0)) * env
		var byte_val := clampi(int((sample * 0.85 + 1.0) * 127.5), 0, 255)
		data[i] = byte_val
	var wav := AudioStreamWAV.new()
	wav.format = AudioStreamWAV.FORMAT_8_BITS
	wav.mix_rate = sample_rate
	wav.stereo = false
	wav.data = data
	return wav


func play_gun_shot() -> void:
	if not is_inside_tree() or _shot_players.is_empty() or _shot_stream == null:
		return
	var player := _shot_players[_shot_player_index]
	_shot_player_index = (_shot_player_index + 1) % _shot_players.size()
	player.pitch_scale = randf_range(0.92, 1.10)
	player.play(0.0)


func _load_stream(path: String, loop: bool) -> AudioStream:
	var stream: AudioStream = null

	if ResourceLoader.exists(path):
		var res := load(path)
		if res is AudioStream:
			stream = res

	if stream == null:
		var file := FileAccess.open(path, FileAccess.READ)
		if file != null:
			var mp3 := AudioStreamMP3.new()
			mp3.data = file.get_buffer(file.get_length())
			mp3.loop = loop
			stream = mp3
		else:
			push_error("AudioManager: Impossible d'ouvrir le fichier audio : %s" % path)
	elif stream is AudioStreamMP3:
		stream.loop = loop
	elif stream is AudioStreamWAV:
		stream.loop_mode = AudioStreamWAV.LOOP_FORWARD if loop else AudioStreamWAV.LOOP_DISABLED

	return stream


func _load_audio_resources() -> void:
	_ambient_streams.clear()
	for path in ambient_track_paths:
		var s := _load_stream(path, false)
		if s != null:
			_ambient_streams.append(s)

	_glitter_stream = _load_stream(glitter_track_path, true)
	if is_instance_valid(_glitter_player) and _glitter_stream != null:
		_glitter_player.stream = _glitter_stream


func start_ambient() -> void:
	if not is_inside_tree() or _ambient_streams.is_empty():
		return

	_active_ambient_index = 0
	_active_player_slot = 0
	_crossfading = false

	var player := _ambient_players[_active_player_slot]
	player.stream = _ambient_streams[_active_ambient_index]
	player.volume_db = -80.0
	player.play(0.0)

	if _ambient_tween and _ambient_tween.is_valid():
		_ambient_tween.kill()
	_ambient_tween = create_tween()
	_ambient_tween.tween_property(player, "volume_db", ambient_volume_db, 3.5)


func _process(_delta: float) -> void:
	if _is_drawing and (Time.get_ticks_msec() - _last_pointing_time_ms) > 400:
		set_drawing(false)

	_check_ambient_crossfade()


func _check_ambient_crossfade() -> void:
	if _ambient_streams.size() < 2 or _crossfading:
		return

	var cur_player := _ambient_players[_active_player_slot]
	if not cur_player.playing or cur_player.stream == null:
		return

	var length := cur_player.stream.get_length()
	var pos := cur_player.get_playback_position()

	var trigger_time := length - ambient_crossfade_time
	if ambient_duration_override > 0.0:
		trigger_time = minf(trigger_time, ambient_duration_override - ambient_crossfade_time)

	if trigger_time > 0.0 and pos >= trigger_time:
		trigger_ambient_crossfade()


func trigger_ambient_crossfade() -> void:
	if not is_inside_tree() or _crossfading or _ambient_streams.size() < 2:
		return

	_crossfading = true
	var cur_player := _ambient_players[_active_player_slot]
	var next_slot := 1 - _active_player_slot
	var next_player := _ambient_players[next_slot]
	var next_index := (_active_ambient_index + 1) % _ambient_streams.size()

	next_player.stream = _ambient_streams[next_index]
	next_player.volume_db = -80.0
	next_player.play(0.0)

	if _ambient_tween and _ambient_tween.is_valid():
		_ambient_tween.kill()
	_ambient_tween = create_tween().set_parallel(true)

	_ambient_tween.tween_property(cur_player, "volume_db", -80.0, ambient_crossfade_time)
	_ambient_tween.tween_property(next_player, "volume_db", ambient_volume_db, ambient_crossfade_time)

	_ambient_tween.chain().tween_callback(func():
		cur_player.stop()
		_active_player_slot = next_slot
		_active_ambient_index = next_index
		_crossfading = false
	)


func _on_player_finished(slot: int) -> void:
	if slot == _active_player_slot and not _crossfading:
		trigger_ambient_crossfade()


## Appelée lors de la détection de Pointing_Up
func set_drawing(drawing: bool) -> void:
	if drawing:
		_last_pointing_time_ms = Time.get_ticks_msec()

	if _is_drawing == drawing:
		return

	_is_drawing = drawing

	if not is_inside_tree() or not is_instance_valid(_glitter_player) or _glitter_stream == null:
		return

	if _glitter_tween and _glitter_tween.is_valid():
		_glitter_tween.kill()
	_glitter_tween = create_tween()

	if _is_drawing:
		var now := Time.get_ticks_msec()
		if (now - _stop_drawing_time_ms) > 2000 or not _glitter_player.playing:
			_glitter_player.play(0.0)
		elif _glitter_player.stream_paused:
			_glitter_player.stream_paused = false

		_glitter_tween.tween_property(_glitter_player, "volume_db", glitter_volume_db, glitter_fade_in_time)
	else:
		_stop_drawing_time_ms = Time.get_ticks_msec()
		_glitter_tween.tween_property(_glitter_player, "volume_db", -80.0, glitter_fade_out_time)
		_glitter_tween.tween_callback(func():
			if not _is_drawing and _glitter_player.playing:
				_glitter_player.stream_paused = true
		)

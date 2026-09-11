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
@export var heart_track_path: String = "res://assets/audio/heart.mp3"
@export var wilhelm_track_path: String = "res://assets/audio/wilhelm_scream.mp3"
@export var shot_track_path: String = "res://assets/audio/Water_drop_trimmed.wav"

@export_group("Volumes (dB)")
@export_range(-80.0, 6.0, 0.5) var ambient_volume_db: float = -8.0
@export_range(-80.0, 6.0, 0.5) var glitter_volume_db: float = -2.0
@export_range(-80.0, 6.0, 0.5) var heart_volume_db: float = -6.0
@export_range(-80.0, 6.0, 0.5) var wilhelm_volume_db: float = 0.0
@export_range(-80.0, 6.0, 0.5) var shot_volume_db: float = -2.0

@export_group("Transitions")
## Durée du crossfade entre deux musiques d'ambiance (en secondes)
@export var ambient_crossfade_time: float = 6.0
## Si > 0, alterne après cette durée au lieu d'attendre la fin de la piste (pour tests/rotation rapide)
@export var ambient_duration_override: float = 0.0
## Durée du fondu d'entrée lorsque l'utilisateur commence à dessiner
@export var glitter_fade_in_time: float = 0.35
## Durée du fondu de sortie lorsque l'utilisateur cesse de dessiner
@export var glitter_fade_out_time: float = 0.70
@export var heart_fade_in_time: float = 0.25
@export var heart_fade_out_time: float = 0.60

var _ambient_players: Array[AudioStreamPlayer] = []
var _ambient_streams: Array[AudioStream] = []
var _active_ambient_index: int = 0
var _active_player_slot: int = 0

var _glitter_player: AudioStreamPlayer
var _glitter_stream: AudioStream
var _glitter_tween: Tween
var _is_drawing: bool = false
var _last_pointing_time_ms: int = 0
var _stop_drawing_time_ms: int = 0

var _heart_music_player: AudioStreamPlayer
var _heart_music_stream: AudioStream
var _heart_music_tween: Tween
var _is_heart_playing: bool = false
var _stop_heart_time_ms: int = 0

var _ambient_tween: Tween
var _crossfading: bool = false

var _shot_players: Array[AudioStreamPlayer] = []
var _shot_stream: AudioStream = null
var _shot_player_index: int = 0

var _heart_players: Array[AudioStreamPlayer] = []
var _heart_stream: AudioStreamWAV = null
var _heart_player_index: int = 0
var _wilhelm_player: AudioStreamPlayer
var _wilhelm_stream: AudioStream
var _last_wilhelm_time_ms: int = -999999


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
	for i in range(12):
		var sp := AudioStreamPlayer.new()
		sp.name = "ShotPlayer%d" % i
		sp.bus = "Master"
		sp.volume_db = shot_volume_db
		sp.stream = _shot_stream
		add_child(sp)
		_shot_players.append(sp)

	_heart_stream = _create_heart_stream()
	for i in range(4):
		var hp := AudioStreamPlayer.new()
		hp.name = "HeartPlayer%d" % i
		hp.bus = "Master"
		hp.volume_db = -3.0
		hp.stream = _heart_stream
		add_child(hp)
		_heart_players.append(hp)

	_heart_music_player = AudioStreamPlayer.new()
	_heart_music_player.name = "HeartMusicPlayer"
	_heart_music_player.bus = "Master"
	_heart_music_player.volume_db = -80.0
	add_child(_heart_music_player)
	_heart_music_player.finished.connect(func():
		if _is_heart_playing:
			_heart_music_player.play(0.0)
	)

	_wilhelm_player = AudioStreamPlayer.new()
	_wilhelm_player.name = "WilhelmPlayer"
	_wilhelm_player.bus = "Master"
	_wilhelm_player.volume_db = wilhelm_volume_db
	add_child(_wilhelm_player)


func _create_shot_stream() -> AudioStreamWAV:
	var sample_rate := 22050
	var duration := 0.16
	var total_samples := int(sample_rate * duration)
	var data := PackedByteArray()
	data.resize(total_samples * 2)
	var phase := 0.0
	for i in range(total_samples):
		var t := float(i) / float(total_samples)
		var freq := 420.0 + 680.0 * (1.0 - exp(-30.0 * t))
		phase += freq / float(sample_rate) * TAU
		var env := sin(clampf(t * 14.0, 0.0, 1.0) * PI * 0.5) * exp(-16.0 * t)
		var sample := sin(phase) * env
		var s16 := clampi(int(sample * 24000.0), -32768, 32767)
		data.encode_s16(i * 2, s16)
	var wav := AudioStreamWAV.new()
	wav.format = AudioStreamWAV.FORMAT_16_BITS
	wav.mix_rate = sample_rate
	wav.stereo = false
	wav.data = data
	return wav


func _create_heart_stream() -> AudioStreamWAV:
	var sample_rate := 22050
	var duration := 0.55
	var total_samples := int(sample_rate * duration)
	var data := PackedByteArray()
	data.resize(total_samples)
	var phase1 := 0.0
	var phase2 := 0.0
	var phase3 := 0.0
	var phase4 := 0.0
	var f1 := 523.25
	var f2 := 659.25
	var f3 := 783.99
	var f4 := 1046.50
	for i in range(total_samples):
		var time_sec := float(i) / float(sample_rate)
		var env1 := exp(-5.0 * time_sec)
		var env2 := exp(-4.5 * maxf(time_sec - 0.04, 0.0)) if time_sec >= 0.04 else 0.0
		var env3 := exp(-4.0 * maxf(time_sec - 0.08, 0.0)) if time_sec >= 0.08 else 0.0
		var env4 := exp(-3.5 * maxf(time_sec - 0.12, 0.0)) if time_sec >= 0.12 else 0.0

		phase1 += f1 / float(sample_rate) * TAU
		phase2 += f2 / float(sample_rate) * TAU
		phase3 += f3 / float(sample_rate) * TAU
		phase4 += f4 / float(sample_rate) * TAU

		var s := 0.28 * sin(phase1) * env1 + 0.28 * sin(phase2) * env2 + 0.26 * sin(phase3) * env3 + 0.22 * sin(phase4) * env4
		var shimmer := 0.06 * sin(phase4 * 2.0) * env4
		var sample := (s + shimmer) * 0.90
		var byte_val := clampi(int((sample + 1.0) * 127.5), 0, 255)
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
	player.volume_db = shot_volume_db + randf_range(-1.5, 1.0)
	player.pitch_scale = randf_range(0.85, 1.30)
	player.play(0.0)


func play_heart_sound() -> void:
	if not is_inside_tree() or _heart_players.is_empty() or _heart_stream == null:
		return
	var player := _heart_players[_heart_player_index]
	_heart_player_index = (_heart_player_index + 1) % _heart_players.size()
	player.pitch_scale = randf_range(0.96, 1.06)
	player.play(0.0)


func play_wilhelm_scream() -> void:
	if not is_inside_tree() or not is_instance_valid(_wilhelm_player) or _wilhelm_stream == null:
		return
	var now := Time.get_ticks_msec()
	if now - _last_wilhelm_time_ms < 150:
		return
	_last_wilhelm_time_ms = now
	_wilhelm_player.volume_db = wilhelm_volume_db
	_wilhelm_player.pitch_scale = randf_range(0.98, 1.02)
	_wilhelm_player.play(0.0)


func _load_stream(path: String, loop: bool) -> AudioStream:
	var stream: AudioStream = null

	if ResourceLoader.exists(path):
		var res := load(path)
		if res is AudioStream:
			stream = res

	if stream == null:
		var file := FileAccess.open(path, FileAccess.READ)
		if file != null:
			var buffer := file.get_buffer(file.get_length())
			if path.to_lower().ends_with(".mp3"):
				var mp3 := AudioStreamMP3.new()
				mp3.data = buffer
				mp3.loop = loop
				stream = mp3
			elif path.to_lower().ends_with(".wav"):
				stream = _load_wav_from_buffer(buffer, loop)
		else:
			push_error("AudioManager: Impossible d'ouvrir le fichier audio : %s" % path)
	elif stream is AudioStreamMP3:
		stream.loop = loop
	elif stream is AudioStreamWAV:
		stream.loop_mode = AudioStreamWAV.LOOP_FORWARD if loop else AudioStreamWAV.LOOP_DISABLED

	return stream


func _load_wav_from_buffer(buffer: PackedByteArray, loop: bool) -> AudioStreamWAV:
	if buffer.size() < 44:
		return null
	var riff := buffer.slice(0, 4).get_string_from_ascii()
	var wave := buffer.slice(8, 12).get_string_from_ascii()
	if riff != "RIFF" or wave != "WAVE":
		return null

	var channels := buffer.decode_u16(22)
	var sample_rate := buffer.decode_u32(24)
	var bits_per_sample := buffer.decode_u16(34)

	var data_offset := 12
	while data_offset < buffer.size() - 8:
		var chunk_id := buffer.slice(data_offset, data_offset + 4).get_string_from_ascii()
		var chunk_size := buffer.decode_u32(data_offset + 4)
		if chunk_id == "data":
			var audio_data := buffer.slice(data_offset + 8, data_offset + 8 + chunk_size)
			var wav := AudioStreamWAV.new()
			wav.data = audio_data
			wav.mix_rate = int(sample_rate)
			wav.stereo = (channels == 2)
			if bits_per_sample == 8:
				wav.format = AudioStreamWAV.FORMAT_8_BITS
			elif bits_per_sample == 16:
				wav.format = AudioStreamWAV.FORMAT_16_BITS
			wav.loop_mode = AudioStreamWAV.LOOP_FORWARD if loop else AudioStreamWAV.LOOP_DISABLED
			return wav
		data_offset += 8 + chunk_size
	return null


func _load_audio_resources() -> void:
	_ambient_streams.clear()
	for path in ambient_track_paths:
		var s := _load_stream(path, false)
		if s != null:
			_ambient_streams.append(s)

	_glitter_stream = _load_stream(glitter_track_path, true)
	if is_instance_valid(_glitter_player) and _glitter_stream != null:
		_glitter_player.stream = _glitter_stream

	_heart_music_stream = _load_stream(heart_track_path, true)
	if _heart_music_stream is AudioStreamMP3:
		(_heart_music_stream as AudioStreamMP3).loop = true
	elif _heart_music_stream is AudioStreamWAV:
		(_heart_music_stream as AudioStreamWAV).loop_mode = AudioStreamWAV.LOOP_FORWARD
	elif _heart_music_stream is AudioStreamOggVorbis:
		(_heart_music_stream as AudioStreamOggVorbis).loop = true
	if is_instance_valid(_heart_music_player) and _heart_music_stream != null:
		_heart_music_player.stream = _heart_music_stream

	_wilhelm_stream = _load_stream(wilhelm_track_path, false)
	if is_instance_valid(_wilhelm_player) and _wilhelm_stream != null:
		_wilhelm_player.stream = _wilhelm_stream

	var loaded_shot := _load_stream(shot_track_path, false)
	if loaded_shot == null and shot_track_path != "res://assets/audio/Water_drop.mp3":
		loaded_shot = _load_stream("res://assets/audio/Water_drop.mp3", false)
	if loaded_shot != null:
		_shot_stream = loaded_shot
		for sp in _shot_players:
			if is_instance_valid(sp):
				sp.stream = _shot_stream


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


## Appelée lors de la détection d'un geste coeur (FINGER_HEART ou TWO_HAND_HEART)
func set_heart_music(active: bool) -> void:
	if _is_heart_playing == active:
		return

	_is_heart_playing = active

	if not is_inside_tree() or not is_instance_valid(_heart_music_player) or _heart_music_stream == null:
		return

	if _heart_music_tween and _heart_music_tween.is_valid():
		_heart_music_tween.kill()
	_heart_music_tween = create_tween()

	if _is_heart_playing:
		var now := Time.get_ticks_msec()
		if (now - _stop_heart_time_ms) > 2000 or not _heart_music_player.playing:
			_heart_music_player.play(0.0)
		elif _heart_music_player.stream_paused:
			_heart_music_player.stream_paused = false

		_heart_music_tween.tween_property(_heart_music_player, "volume_db", heart_volume_db, heart_fade_in_time)
	else:
		_stop_heart_time_ms = Time.get_ticks_msec()
		_heart_music_tween.tween_property(_heart_music_player, "volume_db", -80.0, heart_fade_out_time)
		_heart_music_tween.tween_callback(func():
			if not _is_heart_playing and _heart_music_player.playing:
				_heart_music_player.stream_paused = true
		)

#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Gestionnaire dédié au rendu des tirs de particules pour le geste pistolet (FINGER_GUN).
/// Isole la gestion des pools GPU de projectiles haute vélocité, les étincelles stellaires,
/// la cadence de tir automatique et les éclats de tir (muzzle flare).
/// </summary>
public sealed class GunParticleManager
{
	/// <summary>Intervalle entre chaque coup en tir automatique continu (secondes).</summary>
	public float FireInterval { get; set; } = 0.13f;

	/// <summary>Vélocité minimale des projectiles de tir.</summary>
	public float ProjectileSpeedMin { get; set; } = 900.0f;

	/// <summary>Vélocité maximale des projectiles de tir.</summary>
	public float ProjectileSpeedMax { get; set; } = 1700.0f;

	private readonly struct GunData
	{
		public readonly int TrackId;
		public readonly Vector2 Anchor;
		public readonly Vector2 Direction;

		public GunData(int trackId, Vector2 anchor, Vector2 direction)
		{
			TrackId = trackId;
			Anchor = anchor;
			Direction = direction;
		}
	}

	private readonly List<GunData> _activeGuns = new();
	private readonly Dictionary<int, double> _gunLastShotTimes = new();
	private readonly RandomNumberGenerator _random = new();

	private GpuParticles2D[] _gunProjectileParticleSystems = Array.Empty<GpuParticles2D>();
	private GpuParticles2D[] _gunSparkParticleSystems = Array.Empty<GpuParticles2D>();
	private BodyPalette[] _palettes = Array.Empty<BodyPalette>();
	private float _glowIntensity = 1.15f;

	/// <summary>
	/// Initialise les systèmes de particules GPU associés à chaque palette de joueur.
	/// </summary>
	public void Initialize(
		Node parentNode,
		BodyPalette[] palettes,
		CanvasItemMaterial? canvasMaterial,
		ImageTexture glowTexture,
		ImageTexture sparkleTexture,
		float glowIntensity)
	{
		_random.Randomize();
		_palettes = palettes;
		_glowIntensity = glowIntensity;

		_gunProjectileParticleSystems = new GpuParticles2D[palettes.Length];
		_gunSparkParticleSystems = new GpuParticles2D[palettes.Length];

		for (var i = 0; i < palettes.Length; i++)
		{
			_gunProjectileParticleSystems[i] = CreateGunProjectileParticleSystem(palettes[i], canvasMaterial, glowTexture);
			parentNode.AddChild(_gunProjectileParticleSystems[i]);

			_gunSparkParticleSystems[i] = CreateGunSparkParticleSystem(palettes[i], canvasMaterial, sparkleTexture);
			parentNode.AddChild(_gunSparkParticleSystems[i]);
		}
	}

	/// <summary>
	/// Met à jour les mains en posture de pistolet actives.
	/// </summary>
	public void UpdateGunState(Godot.Collections.Array<Godot.Collections.Dictionary> gunDetections)
	{
		_activeGuns.Clear();
		if (gunDetections == null)
			return;

		foreach (var dict in gunDetections)
		{
			var trackId = dict.ContainsKey("track_id") ? (int)dict["track_id"] : -1;
			var anchor = dict.ContainsKey("anchor") ? (Vector2)dict["anchor"] : Vector2.Zero;
			var dir = dict.ContainsKey("direction") ? (Vector2)dict["direction"] : Vector2.Right;
			_activeGuns.Add(new GunData(trackId, anchor, dir));
		}
	}

	/// <summary>
	/// Cadence et déclenche les tirs automatiques tant que la posture est maintenue.
	/// </summary>
	public void ProcessGunFirings(
		double elapsedTime,
		Func<Vector2, Vector2> normalizedToScreen,
		Func<int, int> getPaletteIndexForTrack,
		Action<int, Vector2, Vector2> onShotFired)
	{
		if (_activeGuns.Count == 0)
			return;

		for (var i = 0; i < _activeGuns.Count; i++)
		{
			var gun = _activeGuns[i];
			var trackId = gun.TrackId;
			var lastShot = _gunLastShotTimes.TryGetValue(trackId, out var time) ? time : -1.0;
			if (lastShot < 0.0 || (elapsedTime - lastShot) >= FireInterval)
			{
				_gunLastShotTimes[trackId] = elapsedTime;
				EmitGunShot(gun.Anchor, gun.Direction, trackId, normalizedToScreen, getPaletteIndexForTrack, onShotFired);
			}
		}

		var staleTracks = new List<int>();
		foreach (var kvp in _gunLastShotTimes)
		{
			var found = false;
			for (var i = 0; i < _activeGuns.Count; i++)
			{
				if (_activeGuns[i].TrackId == kvp.Key)
				{
					found = true;
					break;
				}
			}
			if (!found && (elapsedTime - kvp.Value) > 1.0)
			{
				staleTracks.Add(kvp.Key);
			}
		}
		for (var s = 0; s < staleTracks.Count; s++)
		{
			_gunLastShotTimes.Remove(staleTracks[s]);
		}
	}

	/// <summary>
	/// Émet une salve de tir de particules depuis le bout des doigts dans la direction visée.
	/// </summary>
	public void EmitGunShot(
		Vector2 normalizedAnchor,
		Vector2 normalizedDirection,
		int trackId,
		Func<Vector2, Vector2> normalizedToScreen,
		Func<int, int> getPaletteIndexForTrack,
		Action<int, Vector2, Vector2> onShotFired)
	{
		var screenAnchor = normalizedToScreen(normalizedAnchor);
		var dir = new Vector2(normalizedDirection.X, normalizedDirection.Y);
		if (dir.LengthSquared() < 0.0001f)
		{
			dir = Vector2.Right;
		}
		dir = dir.Normalized();

		var paletteIndex = getPaletteIndexForTrack(trackId) % _palettes.Length;
		var palette = _palettes[paletteIndex];

		// 1. Éclat lumineux au bout des doigts (muzzle flare)
		for (var i = 0; i < 8; i++)
		{
			var flareOffset = RandomOffset(5.0f);
			var flareDrift = dir * _random.RandfRange(30.0f, 90.0f) + RandomOffset(25.0f);
			var flareColor = BoostColor(Colors.White.Lerp(palette.TrailStart, _random.RandfRange(0.0f, 0.4f)), 1.0f);
			_gunProjectileParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(0.0f, screenAnchor + flareOffset),
				flareDrift,
				flareColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
		}

		// 2. Salve de projectiles haute vélocité dans la direction visée
		var projectileCount = _random.RandiRange(22, 32);
		for (var i = 0; i < projectileCount; i++)
		{
			var speed = _random.RandfRange(ProjectileSpeedMin, ProjectileSpeedMax);
			var spreadAngle = _random.RandfRange(-0.09f, 0.09f);
			var particleDir = dir.Rotated(spreadAngle);
			var velocity = particleDir * speed;
			var startOffset = dir * _random.RandfRange(0.0f, 28.0f) + particleDir.Orthogonal() * _random.RandfRange(-3.5f, 3.5f);
			var colorT = _random.Randf();
			var boltColor = BoostColor(
				palette.EvaluateTrail(colorT).Lerp(Colors.White, _random.RandfRange(0.25f, 0.65f)),
				0.98f
			);

			_gunProjectileParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(particleDir.Angle(), screenAnchor + startOffset),
				velocity,
				boltColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
		}

		// 3. Étincelles projetées avec dispersion angulaire plus large
		var sparkCount = _random.RandiRange(16, 24);
		for (var i = 0; i < sparkCount; i++)
		{
			var sparkSpeed = _random.RandfRange(250.0f, 950.0f);
			var sparkSpread = _random.RandfRange(-0.38f, 0.38f);
			var sparkDir = dir.Rotated(sparkSpread);
			var sparkVelocity = sparkDir * sparkSpeed;
			var sparkColor = BoostColor(
				palette.EvaluateTrail(_random.Randf()).Lerp(Colors.White, _random.RandfRange(0.40f, 0.90f)),
				1.0f
			);

			_gunSparkParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(_random.RandfRange(0.0f, Mathf.Tau), screenAnchor + RandomOffset(4.0f)),
				sparkVelocity,
				sparkColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
		}

		onShotFired(trackId, screenAnchor, dir);
	}

	private Vector2 RandomOffset(float radius)
	{
		return new Vector2(
			_random.RandfRange(-radius, radius),
			_random.RandfRange(-radius, radius)
		);
	}

	private Color BoostColor(Color color, float alpha)
	{
		return new Color(
			color.R * _glowIntensity,
			color.G * _glowIntensity,
			color.B * _glowIntensity,
			alpha
		);
	}

	private static GpuParticles2D CreateGunProjectileParticleSystem(BodyPalette palette, CanvasItemMaterial? canvasMaterial, ImageTexture texture)
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.05f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.65f, 0.90f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.45f));
		scaleCurve.AddPoint(new Vector2(0.15f, 0.85f));
		scaleCurve.AddPoint(new Vector2(0.70f, 0.65f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.15f));

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(palette.TrailStart.R, palette.TrailStart.G, palette.TrailStart.B, 1.0f).Lerp(Colors.White, 0.5f));
		gradient.AddPoint(0.50f, new Color(palette.TrailEnd.R, palette.TrailEnd.G, palette.TrailEnd.B, 0.90f));
		gradient.AddPoint(1.0f, new Color(palette.TrailEnd.R, palette.TrailEnd.G, palette.TrailEnd.B, 0.0f));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, 0.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 0.5f,
			DampingMax = 1.5f,
			ScaleMin = 0.40f,
			ScaleMax = 0.85f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = gradient },
			Color = new Color(palette.TrailStart.R, palette.TrailStart.G, palette.TrailStart.B, 1.0f),
		};

		return new GpuParticles2D
		{
			Amount = 18000,
			Lifetime = 0.55f,
			LocalCoords = false,
			Emitting = false,
			Texture = texture,
			ProcessMaterial = processMat,
			Material = canvasMaterial,
			VisibilityRect = new Rect2(-500, -500, 10000, 10000),
		};
	}

	private static GpuParticles2D CreateGunSparkParticleSystem(BodyPalette palette, CanvasItemMaterial? canvasMaterial, ImageTexture texture)
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.08f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.50f, 0.85f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.25f));
		scaleCurve.AddPoint(new Vector2(0.18f, 1.0f));
		scaleCurve.AddPoint(new Vector2(0.60f, 0.65f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.08f));

		var gradient = new Gradient();
		gradient.SetColor(0, new Color(palette.TrailStart.R, palette.TrailStart.G, palette.TrailStart.B, 1.0f).Lerp(Colors.White, 0.6f));
		gradient.AddPoint(0.50f, new Color(palette.ColorNear.R, palette.ColorNear.G, palette.ColorNear.B, 0.90f));
		gradient.AddPoint(1.0f, new Color(palette.ColorFar.R, palette.ColorFar.G, palette.ColorFar.B, 0.0f));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			AngleMin = 0.0f,
			AngleMax = 360.0f,
			AngularVelocityMin = -60.0f,
			AngularVelocityMax = 60.0f,
			Gravity = new Vector3(0.0f, 40.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 2.5f,
			DampingMax = 5.0f,
			ScaleMin = 0.25f,
			ScaleMax = 0.65f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = gradient },
			Color = new Color(palette.TrailStart.R, palette.TrailStart.G, palette.TrailStart.B, 1.0f),
		};

		return new GpuParticles2D
		{
			Amount = 18000,
			Lifetime = 0.38f,
			LocalCoords = false,
			Emitting = false,
			Texture = texture,
			ProcessMaterial = processMat,
			Material = canvasMaterial,
			VisibilityRect = new Rect2(-500, -500, 10000, 10000),
		};
	}
}

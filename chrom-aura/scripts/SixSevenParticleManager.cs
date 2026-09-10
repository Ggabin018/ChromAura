#nullable enable
using Godot;
using System;

/// <summary>
/// Gestionnaire dédié au rendu du geyser d'énergie vertical pour le geste Six-Seven (6-7).
/// Crée un éclat incandescent sur la paume, un faisceau dense de rayons lumineux verticaux
/// fusant vers le ciel et une fontaine d'étincelles divergentes aux couleurs de la palette du joueur.
/// </summary>
public sealed class SixSevenParticleManager
{
	/// <summary>Vélocité minimale des rayons verticaux (pixels/sec).</summary>
	public float BeamSpeedMin { get; set; } = 950.0f;

	/// <summary>Vélocité maximale des rayons verticaux (pixels/sec).</summary>
	public float BeamSpeedMax { get; set; } = 2100.0f;

	/// <summary>Multiplicateur d'intensité lumineuse d'aura.</summary>
	public float GlowIntensity { get; set; } = 1.25f;

	private readonly RandomNumberGenerator _random = new();

	private GpuParticles2D[] _beamParticleSystems = Array.Empty<GpuParticles2D>();
	private GpuParticles2D[] _sparkParticleSystems = Array.Empty<GpuParticles2D>();
	private GpuParticles2D[] _flareParticleSystems = Array.Empty<GpuParticles2D>();
	private BodyPalette[] _palettes = Array.Empty<BodyPalette>();

	/// <summary>
	/// Initialise les systèmes de particules GPU pour chaque palette de corps.
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
		GlowIntensity = glowIntensity;

		var beamTexture = CreateVerticalRayTexture(16, 64);

		_beamParticleSystems = new GpuParticles2D[palettes.Length];
		_sparkParticleSystems = new GpuParticles2D[palettes.Length];
		_flareParticleSystems = new GpuParticles2D[palettes.Length];

		for (var i = 0; i < palettes.Length; i++)
		{
			_flareParticleSystems[i] = CreateFlareParticleSystem(palettes[i], canvasMaterial, glowTexture);
			parentNode.AddChild(_flareParticleSystems[i]);

			_beamParticleSystems[i] = CreateBeamParticleSystem(palettes[i], canvasMaterial, beamTexture);
			parentNode.AddChild(_beamParticleSystems[i]);

			_sparkParticleSystems[i] = CreateSparkParticleSystem(palettes[i], canvasMaterial, sparkleTexture);
			parentNode.AddChild(_sparkParticleSystems[i]);
		}
	}

	/// <summary>
	/// Émet une aura lumineuse continue douce sur les paumes tant que la posture 6-7 est maintenue.
	/// </summary>
	public void EmitSixSevenIdle(
		Vector2 screenAnchor,
		int trackId,
		Func<int, int> getPaletteIndexForTrack)
	{
		if (_palettes.Length == 0 || _beamParticleSystems.Length == 0)
			return;

		var paletteIndex = getPaletteIndexForTrack(trackId) % _palettes.Length;
		var palette = _palettes[paletteIndex];

		// Éclat doux au creux de la paume
		var flareColor = BoostColor(palette.TrailStart.Lerp(Colors.White, 0.45f), 0.85f);
		_flareParticleSystems[paletteIndex].EmitParticle(
			new Transform2D(0.0f, screenAnchor + RandomOffset(6.0f)),
			new Vector2(_random.RandfRange(-6.0f, 6.0f), _random.RandfRange(-35.0f, -10.0f)),
			flareColor,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
		);

		// 1-2 rayons verticaux ascendants
		var speed = _random.RandfRange(400.0f, 850.0f);
		var upwardDir = Vector2.Up.Rotated(_random.RandfRange(-0.08f, 0.08f));
		var rayColor = BoostColor(palette.EvaluateTrail(_random.Randf()).Lerp(Colors.White, 0.5f), 0.90f);
		_beamParticleSystems[paletteIndex].EmitParticle(
			new Transform2D(upwardDir.Angle() + Mathf.Pi * 0.5f, screenAnchor + new Vector2(_random.RandfRange(-12.0f, 12.0f), 0.0f)),
			upwardDir * speed,
			rayColor,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
		);

		// Étincelle volante
		if (_random.Randf() < 0.65f)
		{
			var sparkDir = Vector2.Up.Rotated(_random.RandfRange(-0.35f, 0.35f));
			var sparkColor = BoostColor(palette.ColorNear.Lerp(Colors.White, 0.5f), 0.95f);
			_sparkParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(_random.RandfRange(0.0f, Mathf.Tau), screenAnchor + RandomOffset(4.0f)),
				sparkDir * _random.RandfRange(250.0f, 600.0f),
				sparkColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
		}
	}

	/// <summary>
	/// Déclenche une impulsion de geyser d'énergie vertical au niveau de la paume.
	/// </summary>
	public void EmitSixSevenPulse(
		Vector2 screenAnchor,
		int trackId,
		float impulseStrength,
		Func<int, int> getPaletteIndexForTrack)
	{
		if (_palettes.Length == 0 || _beamParticleSystems.Length == 0)
			return;

		var paletteIndex = getPaletteIndexForTrack(trackId) % _palettes.Length;
		var palette = _palettes[paletteIndex];
		var clampedStrength = Mathf.Clamp(impulseStrength, 0.6f, 1.8f);

		// 1. Orbe / éclat lumineux dense et incandescent sur la paume (base)
		var flareCount = Mathf.RoundToInt(10 * clampedStrength);
		for (var i = 0; i < flareCount; i++)
		{
			var flareOffset = RandomOffset(14.0f);
			var flareDrift = new Vector2(_random.RandfRange(-15.0f, 15.0f), _random.RandfRange(-80.0f, -20.0f));
			var flareColor = BoostColor(
				Colors.White.Lerp(palette.TrailStart, _random.RandfRange(0.0f, 0.35f)),
				1.0f
			);

			_flareParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(0.0f, screenAnchor + flareOffset),
				flareDrift,
				flareColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
		}

		// 2. Faisceau vertical de rayons et colonnes de lumière propulsés vers le haut
		var beamCount = Mathf.RoundToInt(_random.RandiRange(30, 42) * clampedStrength);
		for (var i = 0; i < beamCount; i++)
		{
			var speed = _random.RandfRange(BeamSpeedMin, BeamSpeedMax) * clampedStrength;
			// Dispersion angulaire très étroite : quasi vertical vers le haut (-PI/2)
			var angleSpread = _random.RandfRange(-0.11f, 0.11f);
			var upwardDir = Vector2.Up.Rotated(angleSpread);
			var velocity = upwardDir * speed;

			// Décalage horizontal modéré le long de la paume
			var startOffset = new Vector2(
				_random.RandfRange(-22.0f, 22.0f),
				_random.RandfRange(-6.0f, 6.0f)
			);

			var colorT = _random.Randf();
			var rayColor = BoostColor(
				palette.EvaluateTrail(colorT).Lerp(Colors.White, _random.RandfRange(0.35f, 0.85f)),
				0.98f
			);

			_beamParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(upwardDir.Angle() + Mathf.Pi * 0.5f, screenAnchor + startOffset),
				velocity,
				rayColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
		}

		// 3. Fontaine d'étincelles divergentes et scintillements ascendants
		var sparkCount = Mathf.RoundToInt(_random.RandiRange(26, 38) * clampedStrength);
		for (var i = 0; i < sparkCount; i++)
		{
			var sparkSpeed = _random.RandfRange(350.0f, 1200.0f) * clampedStrength;
			// Éventail d'étincelles plus large (geyser)
			var sparkSpread = _random.RandfRange(-0.48f, 0.48f);
			var sparkDir = Vector2.Up.Rotated(sparkSpread);
			var sparkVelocity = sparkDir * sparkSpeed;

			var sparkColor = BoostColor(
				palette.EvaluateTrail(_random.Randf()).Lerp(Colors.White, _random.RandfRange(0.20f, 0.70f)),
				1.0f
			);

			_sparkParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(_random.RandfRange(0.0f, Mathf.Tau), screenAnchor + RandomOffset(8.0f)),
				sparkVelocity,
				sparkColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
		}
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
			color.R * GlowIntensity,
			color.G * GlowIntensity,
			color.B * GlowIntensity,
			alpha
		);
	}

	private static ImageTexture CreateVerticalRayTexture(int width, int height)
	{
		var image = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
		var centerX = (width - 1) * 0.5f;
		var centerY = (height - 1) * 0.5f;

		for (var y = 0; y < height; y++)
		{
			var ny = Math.Abs(y - centerY) / centerY;
			var vFalloff = Mathf.Pow(Mathf.Clamp(1.0f - ny, 0.0f, 1.0f), 1.2f);

			for (var x = 0; x < width; x++)
			{
				var nx = Math.Abs(x - centerX) / centerX;
				var hFalloff = Mathf.Pow(Mathf.Clamp(1.0f - nx, 0.0f, 1.0f), 2.8f);
				var alpha = Mathf.Clamp(hFalloff * vFalloff * 1.2f, 0.0f, 1.0f);
				image.SetPixel(x, y, new Color(1, 1, 1, alpha));
			}
		}

		return ImageTexture.CreateFromImage(image);
	}

	private static GpuParticles2D CreateFlareParticleSystem(BodyPalette palette, CanvasItemMaterial? canvasMaterial, ImageTexture texture)
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.12f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.55f, 0.75f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.8f));
		scaleCurve.AddPoint(new Vector2(0.20f, 1.35f));
		scaleCurve.AddPoint(new Vector2(0.65f, 0.95f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.3f));

		var gradient = new Gradient();
		gradient.SetColor(0, Colors.White);
		gradient.AddPoint(0.40f, new Color(palette.TrailStart.R, palette.TrailStart.G, palette.TrailStart.B, 0.95f));
		gradient.AddPoint(1.0f, new Color(palette.TrailEnd.R, palette.TrailEnd.G, palette.TrailEnd.B, 0.0f));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, -40.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 2.0f,
			DampingMax = 5.0f,
			ScaleMin = 0.65f,
			ScaleMax = 1.35f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = gradient },
			Color = Colors.White,
		};

		return new GpuParticles2D
		{
			Amount = 4000,
			Lifetime = 0.42f,
			LocalCoords = false,
			Emitting = false,
			Texture = texture,
			ProcessMaterial = processMat,
			Material = canvasMaterial,
			VisibilityRect = new Rect2(-500, -500, 10000, 10000),
		};
	}

	private static GpuParticles2D CreateBeamParticleSystem(BodyPalette palette, CanvasItemMaterial? canvasMaterial, ImageTexture texture)
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.06f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.70f, 0.85f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.40f));
		scaleCurve.AddPoint(new Vector2(0.18f, 1.10f));
		scaleCurve.AddPoint(new Vector2(0.70f, 0.80f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.15f));

		var gradient = new Gradient();
		gradient.SetColor(0, Colors.White.Lerp(palette.TrailStart, 0.4f));
		gradient.AddPoint(0.45f, new Color(palette.TrailStart.R, palette.TrailStart.G, palette.TrailStart.B, 0.95f));
		gradient.AddPoint(0.85f, new Color(palette.TrailEnd.R, palette.TrailEnd.G, palette.TrailEnd.B, 0.70f));
		gradient.AddPoint(1.0f, new Color(palette.TrailEnd.R, palette.TrailEnd.G, palette.TrailEnd.B, 0.0f));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, 20.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 0.8f,
			DampingMax = 2.0f,
			ScaleMin = 0.45f,
			ScaleMax = 1.0f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = gradient },
			Color = Colors.White,
		};

		return new GpuParticles2D
		{
			Amount = 18000,
			Lifetime = 0.65f,
			LocalCoords = false,
			Emitting = false,
			Texture = texture,
			ProcessMaterial = processMat,
			Material = canvasMaterial,
			VisibilityRect = new Rect2(-500, -1000, 10000, 10000),
		};
	}

	private static GpuParticles2D CreateSparkParticleSystem(BodyPalette palette, CanvasItemMaterial? canvasMaterial, ImageTexture texture)
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.08f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.50f, 0.85f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.30f));
		scaleCurve.AddPoint(new Vector2(0.18f, 1.0f));
		scaleCurve.AddPoint(new Vector2(0.65f, 0.60f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.08f));

		var gradient = new Gradient();
		gradient.SetColor(0, Colors.White);
		gradient.AddPoint(0.50f, new Color(palette.ColorNear.R, palette.ColorNear.G, palette.ColorNear.B, 0.90f));
		gradient.AddPoint(1.0f, new Color(palette.ColorFar.R, palette.ColorFar.G, palette.ColorFar.B, 0.0f));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			AngleMin = 0.0f,
			AngleMax = 360.0f,
			AngularVelocityMin = -90.0f,
			AngularVelocityMax = 90.0f,
			Gravity = new Vector3(0.0f, 80.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 1.5f,
			DampingMax = 3.5f,
			ScaleMin = 0.30f,
			ScaleMax = 0.80f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = gradient },
			Color = Colors.White,
		};

		return new GpuParticles2D
		{
			Amount = 18000,
			Lifetime = 0.50f,
			LocalCoords = false,
			Emitting = false,
			Texture = texture,
			ProcessMaterial = processMat,
			Material = canvasMaterial,
			VisibilityRect = new Rect2(-500, -1000, 10000, 10000),
		};
	}
}

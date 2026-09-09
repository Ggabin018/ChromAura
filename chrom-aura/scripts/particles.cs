using Godot;
using System;
using System.Collections.Generic;

public partial class particles : CanvasLayer
{
	[Export] public float TrailLifetime { get; set; } = 4.0f;
	[Export] public float MistLifetime { get; set; } = 0.38f;
	[Export] public float SparkleLifetime { get; set; } = 0.22f;
	[Export] public int ParticlesPerSecond { get; set; } = 38000;
	[Export] public int MaxMistParticles { get; set; } = 35000;
	[Export] public int MaxSparkleParticles { get; set; } = 30000;
	[Export] public int MaxTrailParticles { get; set; } = 25000;
	[Export] public float HandPersistentRadius { get; set; } = 38.0f;
	[Export] public bool PreserveAspectRatio { get; set; } = true;
	[Export] public bool ColorFromDepth { get; set; } = true;
	[Export] public int Stride { get; set; } = 2;
	[Export] public float EtherealGlowIntensity { get; set; } = 1.15f;
	[Export] public float DepthFarThreshold { get; set; } = 0.67f;
	[Export] public float DepthNearThreshold { get; set; } = 0.88f;
	[Export]
	public Color ParticleColorAtMinDepth { get; set; } =
		new Color(180.0f / 255.0f, 250.0f / 255.0f, 255.0f / 255.0f, 0.95f);
	[Export]
	public Color ParticleColorAtMaxDepth { get; set; } =
		new Color(110.0f / 255.0f, 20.0f / 255.0f, 220.0f / 255.0f, 0.95f);

	private readonly List<MaskSample> _maskPoints = new();
	private readonly List<Vector2> _currentFingers = new();
	private readonly List<Vector2> _prevFingerScreenPos = new();
	private readonly RandomNumberGenerator _random = new();

	private GpuParticles2D _mistParticleSystem = null!;
	private GpuParticles2D _sparkleParticleSystem = null!;
	private GpuParticles2D _trailParticleSystem = null!;

	private Vector2I _maskSize = new(640, 480);
	private float _emissionRemainder;
	private bool _isPointingUp;
	private double _lastFingerUpdateTime;
	private double _elapsedTime;

	public override void _Ready()
	{
		_random.Randomize();

		_mistParticleSystem = CreateMistParticleSystem();
		AddChild(_mistParticleSystem);

		_sparkleParticleSystem = CreateSparkleParticleSystem();
		AddChild(_sparkleParticleSystem);

		_trailParticleSystem = CreateTrailParticleSystem();
		AddChild(_trailParticleSystem);
	}

	public override void _Process(double delta)
	{
		_elapsedTime += delta;
		_lastFingerUpdateTime += delta;
		var isPointingActive = _isPointingUp && (_lastFingerUpdateTime < 0.35);

		// Emit persistent trail particles along the pointing finger movement path
		if (isPointingActive && _currentFingers.Count > 0)
		{
			EmitFingerTrails();
		}
		else
		{
			_prevFingerScreenPos.Clear();
		}

		// Emit body / silhouette particles from depth mask
		if (_maskPoints.Count == 0)
			return;

		_emissionRemainder += ParticlesPerSecond * (float)delta;
		var particleCount = Mathf.FloorToInt(_emissionRemainder);
		_emissionRemainder -= particleCount;

		for (var i = 0; i < particleCount; i++)
		{
			EmitSampledParticle(isPointingActive);
		}
	}

	public void UpdatePointingState(bool isPointing, Godot.Collections.Array<Vector2> pointingFingers)
	{
		_isPointingUp = isPointing;
		_currentFingers.Clear();
		if (pointingFingers != null)
		{
			foreach (var f in pointingFingers)
			{
				_currentFingers.Add(f);
			}
		}
		_lastFingerUpdateTime = 0.0;
	}

	public void SetDepthImageMask(Image depthImage)
	{
		if (depthImage == null)
			throw new ArgumentNullException(nameof(depthImage));

		var width = depthImage.GetWidth();
		var height = depthImage.GetHeight();
		_maskPoints.Clear();
		_maskSize = new Vector2I(width, height);

		for (var y = 0; y < height; y += Stride)
		{
			for (var x = 0; x < width; x += Stride)
			{
				var depth = depthImage.GetPixel(x, y).R;
				if (depth <= 0.001f)
					continue;

				var position = new Vector2(
					x + _random.RandfRange(-0.35f, 0.35f) * Stride,
					y + _random.RandfRange(-0.35f, 0.35f) * Stride);

				_maskPoints.Add(new MaskSample(position, depth));
			}
		}
	}
	
	private ShaderMaterial CreateParticleShaderMaterial()
	{
		var shader = new Shader
		{
			Code = @"
shader_type particles;

uniform float damping_min = 2.0;
uniform float damping_max = 5.0;
uniform float scale_min = 0.55;
uniform float scale_max = 1.25;

float rand(float seed)
{
	return fract(sin(seed * 12.9898) * 43758.5453);
}

void start()
{
	// Damping aléatoire par particule, stocké dans un canal
	// CUSTOM libre (on garde CUSTOM.r pour la profondeur)
	float r1 = rand(float(INDEX) + TIME);
	CUSTOM.g = mix(damping_min, damping_max, r1);

	// Scale aléatoire appliqué une fois, à l'émission
	float r2 = rand(float(INDEX) * 1.37 + TIME);
	float s = mix(scale_min, scale_max, r2);
	TRANSFORM[0].xy *= s;
	TRANSFORM[1].xy *= s;

	// On NE TOUCHE PAS à COLOR ni CUSTOM.r ici :
	// ils ont déjà été fixés par EmitParticle() côté C#.
}

void process()
{
	// Gravité nulle : rien à ajouter à VELOCITY.

	// Damping exponentiel (équivalent DampingMin/Max de ParticleProcessMaterial)
	float damping = CUSTOM.g;
	VELOCITY *= exp(-damping * DELTA);

	// Toujours rien touché sur COLOR / CUSTOM.r ici.
}
"
		};

		var material = new ShaderMaterial
		{
			Shader = shader
		};

		material.SetShaderParameter(
			"color_at_min_depth",
			ParticleColorAtMinDepth
		);

		material.SetShaderParameter(
			"color_at_max_depth",
			ParticleColorAtMaxDepth
		);

		return material;
	}

	private void EmitFingerTrails()
	{
		if (_prevFingerScreenPos.Count != _currentFingers.Count)
		{
			_prevFingerScreenPos.Clear();
			foreach (var finger in _currentFingers)
			{
				_prevFingerScreenPos.Add(NormalizedToScreen(finger));
			}
			return;
		}

		for (var i = 0; i < _currentFingers.Count; i++)
		{
			var currentScreen = NormalizedToScreen(_currentFingers[i]);
			var prevScreen = _prevFingerScreenPos[i];
			var distance = prevScreen.DistanceTo(currentScreen);

			if (distance < 500.0f)
			{
				var steps = Mathf.Max(2, Mathf.CeilToInt(distance / 1.8f));
				for (var s = 0; s <= steps; s++)
				{
					var t = (float)s / steps;
					var center = prevScreen.Lerp(currentScreen, t);
					EmitSingleTrailParticle(center);
				}
			}
			else
			{
				for (var s = 0; s < 5; s++)
				{
					EmitSingleTrailParticle(currentScreen);
				}
			}

			_prevFingerScreenPos[i] = currentScreen;
		}
	}

	private void EmitSingleTrailParticle(Vector2 basePosition)
	{
		var offset = new Vector2(
			_random.RandfRange(-2.0f, 2.0f),
			_random.RandfRange(-2.0f, 2.0f));
		var drift = new Vector2(
			_random.RandfRange(-1.5f, 1.5f),
			_random.RandfRange(-3.0f, 1.0f));

		var hueChoice = _random.Randf();
		Color trailColor;
		if (hueChoice < 0.45f)
		{
			trailColor = new Color(0.15f, 0.85f, 1.0f, 0.85f).Lerp(new Color(0.65f, 0.25f, 1.0f, 0.85f), _random.Randf());
		}
		else if (hueChoice < 0.8f)
		{
			trailColor = new Color(1.0f, 0.35f, 0.85f, 0.85f).Lerp(new Color(0.35f, 0.95f, 0.8f, 0.85f), _random.Randf());
		}
		else
		{
			trailColor = new Color(1.0f, 0.92f, 0.55f, 0.90f);
		}

		_trailParticleSystem.EmitParticle(
			new Transform2D(0.0f, basePosition + offset),
			drift,
			trailColor * EtherealGlowIntensity,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color));
	}

	private void EmitSampledParticle(bool isPointingActive)
	{
		var sample = _maskPoints[_random.RandiRange(0, _maskPoints.Count - 1)];
		var screenPos = MaskToScreen(sample.Position);

		// Absolute distance from camera: 0.0 (far ~0.67) -> 1.0 (close ~0.88+)
		var depthSpan = Mathf.Max(0.01f, DepthNearThreshold - DepthFarThreshold);
		var closeness = Mathf.Clamp((sample.NormalizedDepth - DepthFarThreshold) / depthSpan, 0.0f, 1.0f);

		var isNearPointingHand = false;
		if (isPointingActive && _currentFingers.Count > 0)
		{
			foreach (var finger in _currentFingers)
			{
				var fingerMask = new Vector2(finger.X * _maskSize.X, finger.Y * _maskSize.Y);
				if (sample.Position.DistanceTo(fingerMask) <= HandPersistentRadius)
				{
					isNearPointingHand = true;
					break;
				}
			}
		}

		// Far = 100% fine misty soft nebula dust (low sparkle chance 0.01)
		// Close = heavily sparkling crystalline diamonds & glittering starlight (sparkle chance up to 0.85)
		var sparkleChance = Mathf.Lerp(0.01f, 0.85f, Mathf.Pow(closeness, 1.8f));
		if (isNearPointingHand)
		{
			sparkleChance = 0.88f;
		}

		var isSparkle = _random.Randf() < sparkleChance;

		if (isSparkle)
		{
			// Sparkle: Crisp diamond starlight with sparkling twinkle
			var sparkleDrift = new Vector2(
				_random.RandfRange(-2.2f, 2.2f),
				_random.RandfRange(-3.0f, 1.2f));

			Color sparkleColor;
			var sparkleType = _random.Randf();
			if (sparkleType < 0.40f)
				sparkleColor = new Color(1.0f, 1.0f, 1.0f, 0.98f); // Pure diamond starlight
			else if (sparkleType < 0.70f)
				sparkleColor = new Color(0.35f, 0.95f, 1.0f, 0.95f); // Electric diamond cyan
			else if (sparkleType < 0.88f)
				sparkleColor = new Color(1.0f, 0.50f, 0.95f, 0.95f); // Crystal magenta
			else
				sparkleColor = new Color(1.0f, 0.92f, 0.45f, 0.98f); // Radiant gold

			var targetSystem = isNearPointingHand ? _trailParticleSystem : _sparkleParticleSystem;
			
			var color = ParticleColorAtMaxDepth.Lerp(
				ParticleColorAtMinDepth,
				closeness
			);
			
			targetSystem.ProcessMaterial.Set("color", color);
			targetSystem.EmitParticle(
				new Transform2D(0.0f, screenPos + new Vector2(_random.RandfRange(-1.0f, 1.0f), _random.RandfRange(-1.0f, 1.0f))),
				sparkleDrift,
				sparkleColor * EtherealGlowIntensity,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color));
		}
		else
		{
			// Mist: Soft dreamy nebula dust preserving the exact far look
			var wave = (float)Math.Sin(_elapsedTime * 1.5 + sample.Position.Y * 0.008f) * 0.12f;
			var harmonicDepth = Mathf.Clamp(closeness + wave, 0.0f, 1.0f);

			var farMist = new Color(0.28f, 0.12f, 0.65f, 0.32f).Lerp(
				new Color(0.06f, 0.70f, 0.85f, 0.35f),
				harmonicDepth);

			var nearMist = new Color(0.12f, 0.88f, 1.0f, 0.42f).Lerp(
				new Color(0.92f, 0.30f, 0.82f, 0.42f),
				harmonicDepth);

			var mistColor = farMist.Lerp(nearMist, closeness) * EtherealGlowIntensity;

			var mistDrift = new Vector2(
				_random.RandfRange(-0.9f, 0.9f),
				_random.RandfRange(-1.5f, 0.4f));
				
			var color = ParticleColorAtMaxDepth.Lerp(
				ParticleColorAtMinDepth,
				closeness
			);

			_mistParticleSystem.ProcessMaterial.Set("color", color);
			_mistParticleSystem.EmitParticle(
				new Transform2D(0.0f, screenPos + new Vector2(_random.RandfRange(-0.8f, 0.8f), _random.RandfRange(-0.8f, 0.8f))),
				mistDrift,
				mistColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color));
		}
	}

	private Vector2 NormalizedToScreen(Vector2 normalized)
	{
		return MaskToScreen(new Vector2(normalized.X * _maskSize.X, normalized.Y * _maskSize.Y));
	}

	private Vector2 MaskToScreen(Vector2 maskPoint)
	{
		var viewport = GetViewport().GetVisibleRect().Size;
		var scaleX = viewport.X / Mathf.Max(_maskSize.X, 1);
		var scaleY = viewport.Y / Mathf.Max(_maskSize.Y, 1);
		var scale = PreserveAspectRatio ? Mathf.Min(scaleX, scaleY) : 1.0f;

		var x = PreserveAspectRatio
			? (viewport.X - _maskSize.X * scale) * 0.5f + maskPoint.X * scale
			: maskPoint.X * scaleX;
		var y = PreserveAspectRatio
			? (viewport.Y - _maskSize.Y * scale) * 0.5f + maskPoint.Y * scale
			: maskPoint.Y * scaleY;

		return new Vector2(x, y);
	}

	private GpuParticles2D CreateMistParticleSystem()
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.18f, 0.65f));
		alphaCurve.AddPoint(new Vector2(0.65f, 0.55f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var alphaTexture = new CurveTexture { Curve = alphaCurve };

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.35f));
		scaleCurve.AddPoint(new Vector2(0.3f, 0.6f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.25f));

		var scaleTexture = new CurveTexture { Curve = scaleCurve };

		var material = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, -1.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 1.5f,
			DampingMin = 2.0f,
			DampingMax = 4.0f,
			ScaleMin = 0.22f,
			ScaleMax = 0.48f,
			ScaleCurve = scaleTexture,
			AlphaCurve = alphaTexture,
			Color = Colors.White,
		};

		var canvasMaterial = new CanvasItemMaterial
		{
			BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
			LightMode = CanvasItemMaterial.LightModeEnum.Unshaded,
		};

		return new GpuParticles2D
		{
			Material = canvasMaterial,
			Amount = MaxMistParticles,
			Lifetime = MistLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateMistTexture(14),
			ProcessMaterial = material,
			VisibilityRect = new Rect2(-100, -100, 10000, 10000),
		};
	}

	private GpuParticles2D CreateSparkleParticleSystem()
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.10f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.45f, 0.95f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var alphaTexture = new CurveTexture { Curve = alphaCurve };

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.2f));
		scaleCurve.AddPoint(new Vector2(0.20f, 1.0f));
		scaleCurve.AddPoint(new Vector2(0.60f, 0.7f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.1f));

		var scaleTexture = new CurveTexture { Curve = scaleCurve };

		var material = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, -0.8f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 3.0f,
			DampingMin = 3.0f,
			DampingMax = 6.0f,
			ScaleMin = 0.28f,
			ScaleMax = 0.65f,
			ScaleCurve = scaleTexture,
			AlphaCurve = alphaTexture,
			Color = Colors.White,
		};

		var canvasMaterial = new CanvasItemMaterial
		{
			BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
			LightMode = CanvasItemMaterial.LightModeEnum.Unshaded,
		};

		return new GpuParticles2D
		{
			Material = canvasMaterial,
			Amount = MaxSparkleParticles,
			Lifetime = SparkleLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSparkleTexture(16),
			ProcessMaterial = material,
			VisibilityRect = new Rect2(-100, -100, 10000, 10000),
		};
	}

	private GpuParticles2D CreateTrailParticleSystem()
	{
		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 1, 0.95f));
		gradient.AddPoint(0.4f, new Color(1, 1, 1, 0.85f));
		gradient.AddPoint(0.8f, new Color(1, 1, 1, 0.45f));
		gradient.AddPoint(1.0f, new Color(1, 1, 1, 0.0f));

		var colorRamp = new GradientTexture1D
		{
			Gradient = gradient,
		};

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.3f));
		scaleCurve.AddPoint(new Vector2(0.2f, 0.65f));
		scaleCurve.AddPoint(new Vector2(0.7f, 0.4f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.1f));

		var scaleTexture = new CurveTexture { Curve = scaleCurve };

		var material = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, 1.8f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 2.0f,
			DampingMin = 1.5f,
			DampingMax = 3.0f,
			ScaleMin = 0.25f,
			ScaleMax = 0.55f,
			ScaleCurve = scaleTexture,
			ColorRamp = colorRamp,
			Color = Colors.White,
		};

		var canvasMaterial = new CanvasItemMaterial
		{
			BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
			LightMode = CanvasItemMaterial.LightModeEnum.Unshaded,
		};

		return new GpuParticles2D
		{
			Material = canvasMaterial,
			Amount = MaxTrailParticles,
			Lifetime = TrailLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSparkleTexture(14),
			ProcessMaterial = material,
			VisibilityRect = new Rect2(-200, -200, 10000, 10000),
		};
	}

	private static ImageTexture CreateMistTexture(int size)
	{
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		var center = (size - 1) * 0.5f;
		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var distance = new Vector2(x - center, y - center).Length() / center;
				if (distance >= 1.0f)
				{
					image.SetPixel(x, y, new Color(1, 1, 1, 0));
					continue;
				}

				var linear = 1.0f - distance;
				var alpha = Mathf.Pow(linear, 1.8f) * 0.75f;
				image.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp(alpha, 0.0f, 1.0f)));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}

	private static ImageTexture CreateSparkleTexture(int size)
	{
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		var center = (size - 1) * 0.5f;

		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var offset = new Vector2(x - center, y - center);
				var distance = offset.Length() / center;
				if (distance >= 1.0f)
				{
					image.SetPixel(x, y, new Color(1, 1, 1, 0));
					continue;
				}

				var nx = Math.Abs(offset.X) / center;
				var ny = Math.Abs(offset.Y) / center;

				var core = Mathf.Pow(Mathf.Clamp(1.0f - distance * 2.2f, 0.0f, 1.0f), 2.0f);
				var hRay = Mathf.Pow(Mathf.Clamp(1.0f - nx, 0.0f, 1.0f), 1.2f) * Mathf.Pow(Mathf.Clamp(1.0f - ny * 3.5f, 0.0f, 1.0f), 2.0f);
				var vRay = Mathf.Pow(Mathf.Clamp(1.0f - ny, 0.0f, 1.0f), 1.2f) * Mathf.Pow(Mathf.Clamp(1.0f - nx * 3.5f, 0.0f, 1.0f), 2.0f);

				var alpha = Mathf.Clamp(core * 1.3f + hRay * 0.7f + vRay * 0.7f, 0.0f, 1.0f);
				image.SetPixel(x, y, new Color(1, 1, 1, alpha));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}

	private readonly record struct MaskSample(Vector2 Position, float NormalizedDepth);
}

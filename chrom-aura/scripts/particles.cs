using Godot;
using System;
using System.Collections.Generic;

public partial class particles : CanvasLayer
{
	[Export] public float TrailLifetime { get; set; } = 8f;
	[Export] public float BodyLifetime { get; set; } = 0.2f;
	[Export] public int ParticlesPerSecond { get; set; } = 3200;
	[Export] public int MaxBodyParticles { get; set; } = 5000;
	[Export] public int MaxTrailParticles { get; set; } = 25000;
	[Export] public float HandPersistentRadius { get; set; } = 45.0f;
	[Export] public bool PreserveAspectRatio { get; set; } = true;
	[Export] public bool ColorFromDepth { get; set; } = true;
	[Export] public int Stride { get; set; } = 3;

	private readonly List<MaskSample> _maskPoints = new();
	private readonly List<Vector2> _currentFingers = new();
	private readonly List<Vector2> _prevFingerScreenPos = new();
	private readonly RandomNumberGenerator _random = new();

	private GpuParticles2D _particleSystem = null!;
	private GpuParticles2D _trailParticleSystem = null!;
	private Vector2I _maskSize = new(640, 480);
	private float _emissionRemainder;
	private bool _isPointingUp;
	private double _lastFingerUpdateTime;

	public override void _Ready()
	{
		_random.Randomize();
		_particleSystem = CreateBodyParticleSystem();
		AddChild(_particleSystem);

		_trailParticleSystem = CreateTrailParticleSystem();
		AddChild(_trailParticleSystem);
	}

	public override void _Process(double delta)
	{
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

		// Emit body / hand silhouette particles from Kinect depth mask
		if (_maskPoints.Count == 0)
			return;

		_emissionRemainder += ParticlesPerSecond * (float)delta;
		var particleCount = Mathf.FloorToInt(_emissionRemainder);
		_emissionRemainder -= particleCount;
		for (var i = 0; i < particleCount; i++)
			EmitSampledParticle(isPointingActive);
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
				if (depth == 0.0f)
					continue;

				var position = new Vector2(
					x + _random.Randf() * Stride,
					y + _random.Randf() * Stride);
				_maskPoints.Add(new MaskSample(position, depth));
			}
		}
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
				// Interpolate along movement path so rapid strokes stay continuous and smooth
				var steps = Mathf.Max(2, Mathf.CeilToInt(distance / 3.0f));
				for (var s = 0; s <= steps; s++)
				{
					var t = (float)s / steps;
					var center = prevScreen.Lerp(currentScreen, t);
					EmitSingleTrailParticle(center);
				}
			}
			else
			{
				// New hand position / teleport: emit around current position
				for (var s = 0; s < 4; s++)
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
			_random.RandfRange(-6.0f, 6.0f),
			_random.RandfRange(-6.0f, 6.0f));
		var drift = new Vector2(
			_random.RandfRange(-5.0f, 5.0f),
			_random.RandfRange(-6.0f, 4.0f));

		// Radiant cyan to violet drawing color palette
		var color = new Color(0.25f, 0.95f, 1.0f, 0.98f).Lerp(
			new Color(0.85f, 0.35f, 1.0f, 0.98f),
			_random.Randf());

		_trailParticleSystem.EmitParticle(
			new Transform2D(0.0f, basePosition + offset),
			drift,
			color,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color));
	}

	private void EmitSampledParticle(bool isPointingActive)
	{
		var sample = _maskPoints[_random.RandiRange(0, _maskPoints.Count - 1)];
		var screenPos = MaskToScreen(sample.Position);

		// Check if this sample is on or near the pointing hand
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

		var drift = new Vector2(_random.RandfRange(-8.0f, 8.0f), _random.RandfRange(-11.0f, 4.0f));
		var color = ColorFromDepth
			? new Color(0.2f, 0.95f, 1.0f, 0.95f).Lerp(new Color(0.85f, 0.25f, 1.0f, 0.95f), sample.NormalizedDepth)
			: new Color(0.18f, 0.82f, 1.0f, 0.92f);

		var targetSystem = isNearPointingHand ? _trailParticleSystem : _particleSystem;
		var finalDrift = isNearPointingHand ? drift * 0.4f : drift;
		targetSystem.EmitParticle(
			new Transform2D(0.0f, screenPos),
			finalDrift,
			color,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color));
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

	private GpuParticles2D CreateBodyParticleSystem()
	{
		var material = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = Vector3.Zero,
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 2.0f,
			DampingMax = 5.0f,
			ScaleMin = 0.55f,
			ScaleMax = 1.25f,
			Color = Colors.White,
		};

		return new GpuParticles2D
		{
			Amount = MaxBodyParticles,
			Lifetime = BodyLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSoftParticleTexture(20),
			ProcessMaterial = material,
			VisibilityRect = new Rect2(-100, -100, 10000, 10000),
		};
	}

	private GpuParticles2D CreateTrailParticleSystem()
	{
		var gradient = new Gradient();
		gradient.SetColor(0, new Color(1, 1, 1, 1.0f));
		gradient.AddPoint(0.7f, new Color(1, 1, 1, 0.9f));
		gradient.AddPoint(1.0f, new Color(1, 1, 1, 0.0f));

		var colorRamp = new GradientTexture1D
		{
			Gradient = gradient,
		};

		var material = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, 4.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 6.0f,
			DampingMin = 2.0f,
			DampingMax = 5.0f,
			ScaleMin = 0.7f,
			ScaleMax = 1.5f,
			ColorRamp = colorRamp,
			Color = Colors.White,
		};

		return new GpuParticles2D
		{
			Amount = MaxTrailParticles,
			Lifetime = TrailLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSoftParticleTexture(22),
			ProcessMaterial = material,
			VisibilityRect = new Rect2(-200, -200, 10000, 10000),
		};
	}

	private static ImageTexture CreateSoftParticleTexture(int size)
	{
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		var center = (size - 1) * 0.5f;
		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var distance = new Vector2(x - center, y - center).Length() / center;
				var alpha = Mathf.Clamp(1.0f - distance, 0.0f, 1.0f);
				image.SetPixel(x, y, new Color(1, 1, 1, alpha * alpha));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}

	private readonly record struct MaskSample(Vector2 Position, float NormalizedDepth);
}

#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Nuage persistant de lucioles ambiantes. Le mouvement est simulé sur CPU et rendu
/// en un seul lot via MultiMesh. Un champ basse résolution dérivé du masque Kinect
/// fournit une normale de répulsion, complétée par la vitesse des corps suivis.
/// </summary>
public sealed partial class AmbientFireflies : MultiMeshInstance2D
{
	private const int FieldCellSize = 8;
	private const int GlowTextureSize = 24;

	private struct Firefly
	{
		public Vector2 Position;
		public Vector2 Velocity;
		public float Phase;
		public float WanderFrequency;
		public float Size;
		public Color Color;
	}

	private readonly struct BodyMotion
	{
		public BodyMotion(Vector2 centroid, Vector2 velocity)
		{
			Centroid = centroid;
			Velocity = velocity;
		}

		public Vector2 Centroid { get; }
		public Vector2 Velocity { get; }
	}

	private readonly RandomNumberGenerator _random = new();
	private readonly List<BodyMotion> _bodyMotions = new();
	private Firefly[] _fireflies = Array.Empty<Firefly>();
	private float[] _density = Array.Empty<float>();
	private float[] _scratch = Array.Empty<float>();
	private bool[] _occupancy = Array.Empty<bool>();
	private Vector2I _maskSize = new(640, 480);
	private Vector2 _lastViewportSize;
	private int _fieldWidth;
	private int _fieldHeight;
	private double _elapsedTime;

	public int ParticleCount { get; set; } = 250;
	public float AmbientSpeed { get; set; } = 12.0f;
	public float InfluenceRadius { get; set; } = 48.0f;
	public float RepulsionStrength { get; set; } = 82.0f;
	public float BodyWindInfluence { get; set; } = 0.25f;
	public float ParticleSizeMin { get; set; } = 3.0f;
	public float ParticleSizeMax { get; set; } = 8.0f;
	public float TwinkleSpeed { get; set; } = 1.0f;
	public float AmbientOpacity { get; set; } = 0.34f;
	public bool PreserveAspectRatio { get; set; } = true;

	public void Initialize(bool additiveBlending)
	{
		_random.Randomize();
		ZIndex = -10;
		Texture = CreateGlowTexture(GlowTextureSize);

		if (additiveBlending)
		{
			Material = new CanvasItemMaterial
			{
				BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
				LightMode = CanvasItemMaterial.LightModeEnum.Unshaded,
			};
		}

		CreateInstances(Mathf.Max(1, ParticleCount));
		SetProcess(true);
	}

	/// <summary>
	/// Reconstruit le champ d'influence à la cadence de la caméra de profondeur.
	/// La simulation des lucioles continue indépendamment à la cadence d'affichage.
	/// </summary>
	public void UpdateSilhouette(
		IReadOnlyList<MaskSample> maskPoints,
		IReadOnlyList<TrackedBody> trackedBodies,
		Vector2I maskSize)
	{
		_maskSize = new Vector2I(Mathf.Max(1, maskSize.X), Mathf.Max(1, maskSize.Y));
		EnsureFieldStorage();
		Array.Clear(_occupancy);

		for (var i = 0; i < maskPoints.Count; i++)
		{
			var point = maskPoints[i].Position;
			var gx = Mathf.Clamp(Mathf.FloorToInt(point.X / FieldCellSize), 0, _fieldWidth - 1);
			var gy = Mathf.Clamp(Mathf.FloorToInt(point.Y / FieldCellSize), 0, _fieldHeight - 1);
			_occupancy[gy * _fieldWidth + gx] = true;
		}

		_bodyMotions.Clear();
		for (var i = 0; i < trackedBodies.Count; i++)
		{
			var body = trackedBodies[i];
			if (body.MissedFrames == 0)
				_bodyMotions.Add(new BodyMotion(body.Centroid, body.Velocity));
		}

		BuildDensityField();
	}

	public override void _Process(double delta)
	{
		if (_fireflies.Length == 0 || Multimesh == null)
			return;

		var viewportSize = GetViewport().GetVisibleRect().Size;
		if (viewportSize.X <= 1.0f || viewportSize.Y <= 1.0f)
			return;

		HandleViewportResize(viewportSize);
		_elapsedTime += delta;
		var dt = Mathf.Min((float)delta, 1.0f / 20.0f);
		var damping = Mathf.Exp(-1.25f * dt);
		var maxSpeed = Mathf.Max(AmbientSpeed * 6.0f, 40.0f);

		for (var i = 0; i < _fireflies.Length; i++)
		{
			var firefly = _fireflies[i];
			var time = (float)_elapsedTime;
			var wander = new Vector2(
				Mathf.Cos(time * firefly.WanderFrequency + firefly.Phase),
				Mathf.Sin(time * firefly.WanderFrequency * 0.83f + firefly.Phase * 1.71f)
			) * (AmbientSpeed * 0.72f);

			var acceleration = wander;
			AddSilhouetteForces(firefly.Position, viewportSize, ref acceleration);

			firefly.Velocity = firefly.Velocity * damping + acceleration * dt;
			if (firefly.Velocity.LengthSquared() > maxSpeed * maxSpeed)
				firefly.Velocity = firefly.Velocity.Normalized() * maxSpeed;

			firefly.Position += firefly.Velocity * dt;
			WrapPosition(ref firefly.Position, viewportSize);

			var twinkle = 0.62f + 0.38f * Mathf.Sin(
				time * firefly.WanderFrequency * 2.2f * TwinkleSpeed + firefly.Phase
			);
			var pulseScale = 0.88f + twinkle * 0.18f;
			var instanceScale = firefly.Size * pulseScale / GlowTextureSize;
			Multimesh.SetInstanceTransform2D(
				i,
				new Transform2D(0.0f, new Vector2(instanceScale, instanceScale), 0.0f, firefly.Position)
			);
			var color = firefly.Color;
			color.A = AmbientOpacity * Mathf.Lerp(0.45f, 1.0f, twinkle);
			Multimesh.SetInstanceColor(i, color);
			_fireflies[i] = firefly;
		}
	}

	private void CreateInstances(int count)
	{
		_fireflies = new Firefly[count];
		var multiMesh = new MultiMesh
		{
			TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
			UseColors = true,
			Mesh = new QuadMesh
			{
				Size = new Vector2(GlowTextureSize, GlowTextureSize),
			},
			InstanceCount = count,
		};
		Multimesh = multiMesh;

		var viewportSize = GetViewport().GetVisibleRect().Size;
		if (viewportSize.X <= 1.0f || viewportSize.Y <= 1.0f)
			viewportSize = new Vector2(1920.0f, 1080.0f);
		_lastViewportSize = viewportSize;

		for (var i = 0; i < count; i++)
		{
			var angle = _random.RandfRange(0.0f, Mathf.Tau);
			var colorMix = _random.Randf();
			_fireflies[i] = new Firefly
			{
				Position = new Vector2(
					_random.RandfRange(0.0f, viewportSize.X),
					_random.RandfRange(0.0f, viewportSize.Y)
				),
				Velocity = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle))
					* _random.RandfRange(AmbientSpeed * 0.35f, AmbientSpeed),
				Phase = _random.RandfRange(0.0f, Mathf.Tau),
				WanderFrequency = _random.RandfRange(0.32f, 0.78f),
				Size = _random.RandfRange(ParticleSizeMin, ParticleSizeMax),
				Color = new Color(
					Mathf.Lerp(0.62f, 0.92f, colorMix),
					Mathf.Lerp(0.86f, 1.0f, colorMix),
					1.0f,
					AmbientOpacity
				),
			};
		}
	}

	private void EnsureFieldStorage()
	{
		var width = Mathf.Max(1, Mathf.CeilToInt(_maskSize.X / (float)FieldCellSize));
		var height = Mathf.Max(1, Mathf.CeilToInt(_maskSize.Y / (float)FieldCellSize));
		var length = width * height;
		if (width == _fieldWidth && height == _fieldHeight && _density.Length == length)
			return;

		_fieldWidth = width;
		_fieldHeight = height;
		_density = new float[length];
		_scratch = new float[length];
		_occupancy = new bool[length];
	}

	private void BuildDensityField()
	{
		Array.Clear(_density);
		if (_fieldWidth == 0 || _fieldHeight == 0)
			return;

		var viewportSize = GetViewport().GetVisibleRect().Size;
		var screenScale = GetMaskToScreenScale(viewportSize);
		var influenceInMask = InfluenceRadius / Mathf.Max(screenScale.X, 0.001f);
		var radius = Mathf.Clamp(Mathf.CeilToInt(influenceInMask / FieldCellSize), 1, 10);
		var radiusSquared = radius * radius;

		for (var y = 0; y < _fieldHeight; y++)
		{
			for (var x = 0; x < _fieldWidth; x++)
			{
				var index = y * _fieldWidth + x;
				if (_occupancy[index])
				{
					_density[index] = 1.0f;
					continue;
				}

				var nearestSquared = int.MaxValue;
				for (var oy = -radius; oy <= radius; oy++)
				{
					var sy = y + oy;
					if (sy < 0 || sy >= _fieldHeight)
						continue;

					for (var ox = -radius; ox <= radius; ox++)
					{
						var distanceSquared = ox * ox + oy * oy;
						if (distanceSquared > radiusSquared || distanceSquared >= nearestSquared)
							continue;
						var sx = x + ox;
						if (sx >= 0 && sx < _fieldWidth && _occupancy[sy * _fieldWidth + sx])
							nearestSquared = distanceSquared;
					}
				}

				if (nearestSquared != int.MaxValue)
					_density[index] = 1.0f - Mathf.Sqrt(nearestSquared) / (radius + 1.0f);
			}
		}

		// Un lissage court retire les marches de la grille sans effacer la forme du corps.
		for (var y = 0; y < _fieldHeight; y++)
		{
			for (var x = 0; x < _fieldWidth; x++)
			{
				var sum = 0.0f;
				var samples = 0;
				for (var oy = -1; oy <= 1; oy++)
				{
					var sy = y + oy;
					if (sy < 0 || sy >= _fieldHeight)
						continue;
					for (var ox = -1; ox <= 1; ox++)
					{
						var sx = x + ox;
						if (sx < 0 || sx >= _fieldWidth)
							continue;
						sum += _density[sy * _fieldWidth + sx];
						samples++;
					}
				}
				_scratch[y * _fieldWidth + x] = sum / Mathf.Max(samples, 1);
			}
		}

		(_density, _scratch) = (_scratch, _density);
	}

	private void AddSilhouetteForces(Vector2 screenPosition, Vector2 viewportSize, ref Vector2 acceleration)
	{
		if (_density.Length == 0 || _bodyMotions.Count == 0)
			return;

		var maskPosition = ScreenToMask(screenPosition, viewportSize);
		if (maskPosition.X < 0.0f || maskPosition.Y < 0.0f
			|| maskPosition.X >= _maskSize.X || maskPosition.Y >= _maskSize.Y)
			return;

		var gridPosition = maskPosition / FieldCellSize;
		var density = SampleDensity(gridPosition.X, gridPosition.Y);
		if (density < 0.015f)
			return;

		var left = SampleDensity(gridPosition.X - 1.0f, gridPosition.Y);
		var right = SampleDensity(gridPosition.X + 1.0f, gridPosition.Y);
		var up = SampleDensity(gridPosition.X, gridPosition.Y - 1.0f);
		var down = SampleDensity(gridPosition.X, gridPosition.Y + 1.0f);
		var outward = new Vector2(left - right, up - down);

		var nearest = _bodyMotions[0];
		var nearestDistanceSquared = maskPosition.DistanceSquaredTo(nearest.Centroid);
		for (var i = 1; i < _bodyMotions.Count; i++)
		{
			var candidate = _bodyMotions[i];
			var distanceSquared = maskPosition.DistanceSquaredTo(candidate.Centroid);
			if (distanceSquared < nearestDistanceSquared)
			{
				nearest = candidate;
				nearestDistanceSquared = distanceSquared;
			}
		}

		if (outward.LengthSquared() < 0.0001f)
			outward = maskPosition - nearest.Centroid;
		var scale = GetMaskToScreenScale(viewportSize);
		outward = new Vector2(outward.X * scale.X, outward.Y * scale.Y);
		if (outward.LengthSquared() > 0.0001f)
			outward = outward.Normalized();
		var bodyVelocityScreen = new Vector2(nearest.Velocity.X * scale.X, nearest.Velocity.Y * scale.Y);
		acceleration += outward * (RepulsionStrength * density);
		acceleration += bodyVelocityScreen * (BodyWindInfluence * density);
	}

	private float SampleDensity(float x, float y)
	{
		x = Mathf.Clamp(x, 0.0f, _fieldWidth - 1.0f);
		y = Mathf.Clamp(y, 0.0f, _fieldHeight - 1.0f);
		var x0 = Mathf.FloorToInt(x);
		var y0 = Mathf.FloorToInt(y);
		var x1 = Mathf.Min(x0 + 1, _fieldWidth - 1);
		var y1 = Mathf.Min(y0 + 1, _fieldHeight - 1);
		var tx = x - x0;
		var ty = y - y0;
		var top = Mathf.Lerp(_density[y0 * _fieldWidth + x0], _density[y0 * _fieldWidth + x1], tx);
		var bottom = Mathf.Lerp(_density[y1 * _fieldWidth + x0], _density[y1 * _fieldWidth + x1], tx);
		return Mathf.Lerp(top, bottom, ty);
	}

	private void HandleViewportResize(Vector2 viewportSize)
	{
		if (_lastViewportSize.X <= 1.0f || _lastViewportSize.Y <= 1.0f)
		{
			_lastViewportSize = viewportSize;
			return;
		}
		if (_lastViewportSize.IsEqualApprox(viewportSize))
			return;

		var ratio = viewportSize / _lastViewportSize;
		for (var i = 0; i < _fireflies.Length; i++)
		{
			var firefly = _fireflies[i];
			firefly.Position *= ratio;
			_fireflies[i] = firefly;
		}
		_lastViewportSize = viewportSize;
	}

	private static void WrapPosition(ref Vector2 position, Vector2 viewportSize)
	{
		const float margin = 12.0f;
		if (position.X < -margin) position.X = viewportSize.X + margin;
		else if (position.X > viewportSize.X + margin) position.X = -margin;
		if (position.Y < -margin) position.Y = viewportSize.Y + margin;
		else if (position.Y > viewportSize.Y + margin) position.Y = -margin;
	}

	private Vector2 ScreenToMask(Vector2 screenPosition, Vector2 viewportSize)
	{
		var scale = GetMaskToScreenScale(viewportSize);
		if (!PreserveAspectRatio)
			return new Vector2(screenPosition.X / scale.X, screenPosition.Y / scale.Y);

		var offset = new Vector2(
			(viewportSize.X - _maskSize.X * scale.X) * 0.5f,
			(viewportSize.Y - _maskSize.Y * scale.Y) * 0.5f
		);
		return (screenPosition - offset) / scale;
	}

	private Vector2 GetMaskToScreenScale(Vector2 viewportSize)
	{
		var scaleX = viewportSize.X / Mathf.Max(_maskSize.X, 1);
		var scaleY = viewportSize.Y / Mathf.Max(_maskSize.Y, 1);
		if (!PreserveAspectRatio)
			return new Vector2(scaleX, scaleY);
		var uniformScale = Mathf.Min(scaleX, scaleY);
		return new Vector2(uniformScale, uniformScale);
	}

	private static ImageTexture CreateGlowTexture(int size)
	{
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		var center = (size - 1) * 0.5f;
		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var distance = new Vector2(x - center, y - center).Length() / center;
				var core = Mathf.Pow(Mathf.Clamp(1.0f - distance * 2.1f, 0.0f, 1.0f), 2.0f);
				var halo = Mathf.Pow(Mathf.Clamp(1.0f - distance, 0.0f, 1.0f), 2.8f) * 0.58f;
				image.SetPixel(x, y, new Color(1.0f, 1.0f, 1.0f, Mathf.Clamp(core + halo, 0.0f, 1.0f)));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}
}

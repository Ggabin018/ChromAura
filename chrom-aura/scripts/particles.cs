using Godot;
using System;
using System.Collections.Generic;

public partial class particles : CanvasLayer
{
	[Export]
	public Color ParticleColorAtMinDepth { get; set; } =
		new Color(180.0f / 255.0f, 250.0f / 255.0f, 255.0f / 255.0f, 0.95f);
	
	[Export]
	public Color ParticleColorAtMaxDepth { get; set; } =
		new Color(110.0f / 255.0f, 20.0f / 255.0f, 220.0f / 255.0f, 0.95f);
	
	private const int ParticlesPerSecond = 3200;
	private const int MaxParticles = 5000;
	private const float ParticleLifetime = 0.2f;
	private const bool PreserveAspectRatio = true;
	private const bool ColorFromDepth = true;
	private const int Stride = 3;

	private readonly List<MaskSample> _maskPoints = new();
	private readonly RandomNumberGenerator _random = new();
	private GpuParticles2D _particleSystem = null!;
	private Vector2I _maskSize;
	private float _emissionRemainder;

	public override void _Ready()
	{
		_random.Randomize();
		_particleSystem = CreateParticleSystem();
		AddChild(_particleSystem);
	}

	public override void _Process(double delta)
	{
		if (_maskPoints.Count == 0)
			return;

		_emissionRemainder += ParticlesPerSecond * (float)delta;
		var particleCount = Mathf.FloorToInt(_emissionRemainder);
		_emissionRemainder -= particleCount;
		for (var i = 0; i < particleCount; i++)
			EmitSampledParticle();
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

	private GpuParticles2D CreateParticleSystem()
	{
		var particleMaterial = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = Vector3.Zero,
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 2.0f,
			DampingMax = 5.0f,
			ScaleMin = 0.55f,
			ScaleMax = 1.25f,
		};
		
		var particleSystem = new GpuParticles2D
		{
			Amount = MaxParticles,
			Lifetime = ParticleLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSoftParticleTexture(20),
			ProcessMaterial = particleMaterial,
			VisibilityRect = new Rect2(-100, -100, 10000, 10000),
		};

		return particleSystem;
	}

	private void EmitSampledParticle()
	{
		var sample = _maskPoints[_random.RandiRange(0, _maskPoints.Count - 1)];
		var viewport = GetViewport().GetVisibleRect().Size;
		var scaleX = viewport.X / _maskSize.X;
		var scaleY = viewport.Y / _maskSize.Y;
		var scale = PreserveAspectRatio ? Mathf.Min(scaleX, scaleY) : 1.0f;
		var x = PreserveAspectRatio
			? (viewport.X - _maskSize.X * scale) * 0.5f + sample.Position.X * scale
			: sample.Position.X * scaleX;
		var y = PreserveAspectRatio
			? (viewport.Y - _maskSize.Y * scale) * 0.5f + sample.Position.Y * scale
			: sample.Position.Y * scaleY;
		var drift = new Vector2(_random.RandfRange(-9f, 9f), _random.RandfRange(-12f, 4f));
		
		var particleColor = ParticleColorAtMaxDepth.Lerp(
			ParticleColorAtMinDepth,
			sample.NormalizedDepth
		);
		var particleMaterial = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = Vector3.Zero,
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			DampingMin = 2.0f,
			DampingMax = 5.0f,
			ScaleMin = 0.55f,
			ScaleMax = 1.25f,
			Color = particleColor,
		};
		
		_particleSystem.ProcessMaterial = particleMaterial;

		var customData = new Color(
			0.0f,
			0.0f,
			0.0f,
			ParticleLifetime
		);

		_particleSystem.EmitParticle(
			new Transform2D(0.0f, new Vector2(x, y)),
			drift,
			particleColor,
			Colors.White,
			(uint)(
				GpuParticles2D.EmitFlags.Position |
				GpuParticles2D.EmitFlags.Velocity |
				GpuParticles2D.EmitFlags.Color
			)
		);
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

#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Gestionnaire principal de rendu de particules (ChromAura).
/// Gère les systèmes de particules GPU pour la silhouette corporelle et les tracés de dessin,
/// applique les palettes de couleurs par corps détecté et délègue la segmentation à BodyDetector.
/// </summary>
public partial class particles : CanvasLayer
{
	/// <summary>Émis lorsque le nombre de corps détectés change.</summary>
	[Signal] public delegate void BodyCountChangedEventHandler(int count);

	// =========================================================================
	// PARAMÈTRES EXPORTÉS (Inspecteur Godot)
	// =========================================================================

	[ExportGroup("Durées de vie")]
	/// <summary>Durée de vie (secondes) des particules de tracé de dessin persistant.</summary>
	[Export] public float TrailLifetime { get; set; } = 8.0f;

	/// <summary>Durée de vie (secondes) de la brume qui dessine la silhouette.</summary>
	[Export] public float MistLifetime { get; set; } = 0.38f;

	/// <summary>Durée de vie (secondes) des scintillements de la silhouette.</summary>
	[Export] public float SparkleLifetime { get; set; } = 0.22f;

	[ExportGroup("Débits et Limites")]
	/// <summary>Nombre de particules de silhouette émises par seconde.</summary>
	[Export] public int ParticlesPerSecond { get; set; } = 38000;

	/// <summary>Capacité maximale du pool de brume pour une palette.</summary>
	[Export] public int MaxMistParticles { get; set; } = 35000;

	/// <summary>Capacité maximale du pool de scintillements pour une palette.</summary>
	[Export] public int MaxSparkleParticles { get; set; } = 30000;

	/// <summary>Capacité maximale du pool de tracé pour une palette.</summary>
	[Export] public int MaxTrailParticles { get; set; } = 25000;

	[ExportGroup("Détection et Interaction")]
	/// <summary>Rayon d'influence (pixels masque) autour d'un doigt pointé pour émettre des particules de tracé.</summary>
	[Export] public float HandPersistentRadius { get; set; } = 45.0f;

	/// <summary>Conserve le ratio d'aspect de la caméra lors de la conversion vers l'écran.</summary>
	[Export] public bool PreserveAspectRatio { get; set; } = true;

	/// <summary>Interpole la couleur de chaque particule selon la profondeur mesurée.</summary>
	[Export] public bool ColorFromDepth { get; set; } = true;

	/// <summary>Pas de sous-échantillonnage de la grille pour le clustering de corps.</summary>
	[Export] public int ClusterStride { get; set; } = 4;

	/// <summary>Discontinuité maximale de profondeur Z autorisée entre deux pixels d'un même corps.</summary>
	[Export] public float MaxDepthDiscontinuity { get; set; } = 0.14f;

	/// <summary>Nombre minimal de pixels pour valider la détection d'un corps (filtre de bruit).</summary>
	[Export] public int MinClusterPixels { get; set; } = 20;

	/// <summary>Distance maximale (pixels) pour associer un corps d'une trame à la suivante.</summary>
	[Export] public float MaxTrackingDistance { get; set; } = 160.0f;

	/// <summary>Nombre maximal de trames d'absence avant de supprimer un corps non détecté.</summary>
	[Export] public int MaxMissedFrames { get; set; } = 15;

	[ExportGroup("Rendu et Débug")]
	/// <summary>Active le mode de mélange additif pour un effet d'aura lumineux et bioluminescent.</summary>
	[Export] public bool AdditiveBlending { get; set; } = true;

	/// <summary>Intensité lumineuse appliquée aux couleurs des palettes.</summary>
	[Export] public float EtherealGlowIntensity { get; set; } = 1.15f;

	/// <summary>Affiche l'overlay de débug (boîtes englobantes et centroïdes). Désactivé par défaut (Touche D).</summary>
	[Export] public bool ShowBodyDebug { get; set; } = false;

	/// <summary>Décalage manuel des palettes (Touche P) pour tester les 5 palettes avec un seul utilisateur.</summary>
	[Export] public int PaletteOffset { get; set; } = 0;

	// =========================================================================
	// PROPRIÉTÉS PUBLIQUES
	// =========================================================================

	/// <summary>Nombre de corps actuellement détectés et suivis.</summary>
	public int DetectedBodyCount => _bodyDetector.DetectedBodyCount;

	/// <summary>Accès en lecture seule à la liste des corps suivis.</summary>
	public IReadOnlyList<TrackedBody> TrackedBodies => _bodyDetector.TrackedBodies;

	// =========================================================================
	// ÉTAT INTERNE
	// =========================================================================

	private readonly BodyDetector _bodyDetector = new();
	private readonly List<MaskSample> _maskPoints = new();
	private readonly List<Vector2> _currentFingers = new();
	private readonly List<int> _fingerPaletteIndices = new();
	private readonly List<Vector2> _prevFingerScreenPos = new();
	private readonly RandomNumberGenerator _random = new();

	// Un seul pipeline visuel brume/scintillement pour le corps,
	// et un double pipeline (cœur fluide lumineux + poussière d'étoiles) pour le tracé de dessin.
	private readonly GpuParticles2D[] _mistParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _sparkleParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _trailCoreParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _trailSparkleParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];

	private readonly List<Vector2> _smoothedFingerPos = new();

	private BodyDebugOverlay _debugOverlay = null!;
	private Vector2I _maskSize = new(640, 480);
	private float _emissionRemainder;
	private bool _isPointingUp;
	private double _lastFingerUpdateTime;
	private double _elapsedTime;
	private int _lastReportedBodyCount = -1;

	// =========================================================================
	// CYCLE DE VIE GODOT
	// =========================================================================

	public override void _Ready()
	{
		_random.Randomize();

		var palettes = BodyPalette.DefaultPalettes;
		for (var i = 0; i < palettes.Length; i++)
		{
			_mistParticleSystems[i] = CreateMistParticleSystem(palettes[i]);
			AddChild(_mistParticleSystems[i]);

			_sparkleParticleSystems[i] = CreateSparkleParticleSystem(palettes[i]);
			AddChild(_sparkleParticleSystems[i]);

			_trailCoreParticleSystems[i] = CreateTrailCoreParticleSystem(palettes[i]);
			AddChild(_trailCoreParticleSystems[i]);

			_trailSparkleParticleSystems[i] = CreateTrailSparkleParticleSystem(palettes[i]);
			AddChild(_trailSparkleParticleSystems[i]);
		}

		// Initialisation de l'overlay de débug séparé
		_debugOverlay = new BodyDebugOverlay();
		_debugOverlay.Initialize(this);
		_debugOverlay.IsVisibleInGame = ShowBodyDebug;
		AddChild(_debugOverlay);
	}

	public override void _Process(double delta)
	{
		_elapsedTime += delta;
		_lastFingerUpdateTime += delta;
		var isPointingActive = _isPointingUp && (_lastFingerUpdateTime < 0.35);

		// 1. Émission des tracés lumineux le long de la trajectoire des doigts pointés
		if (isPointingActive && _currentFingers.Count > 0)
		{
			EmitFingerTrails();
		}
		else
		{
			_prevFingerScreenPos.Clear();
			_smoothedFingerPos.Clear();
		}

		// 2. Émission des particules de silhouette corporelle
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

	/// <summary>
	/// Gestion des raccourcis clavier utilisateur pour le contrôle en temps réel.
	/// </summary>
	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
		{
			// Touche D : Bascule de l'affichage des boîtes de débug
			if (keyEvent.Keycode == Key.D)
			{
				ShowBodyDebug = !ShowBodyDebug;
				if (_debugOverlay != null)
				{
					_debugOverlay.IsVisibleInGame = ShowBodyDebug;
					_debugOverlay.QueueRedraw();
				}
			}
			// Touche P : Cycle manuel des palettes pour prévisualisation
			else if (keyEvent.Keycode == Key.P)
			{
				var palettes = BodyPalette.DefaultPalettes;
				PaletteOffset = (PaletteOffset + 1) % palettes.Length;
				for (var i = 0; i < _bodyDetector.TrackedBodies.Count; i++)
				{
					_bodyDetector.TrackedBodies[i].PaletteIndex =
						(_bodyDetector.TrackedBodies[i].Id - 1 + PaletteOffset) % palettes.Length;
				}
				EmitSignal(SignalName.BodyCountChanged, _bodyDetector.DetectedBodyCount);
			}
		}
	}

	// =========================================================================
	// API PUBLIQUE (Appelée depuis GDScript / camera_script.gd)
	// =========================================================================

	/// <summary>
	/// Met à jour l'état de détection du geste 'Pointing_Up' et les coordonnées des doigts pointés.
	/// </summary>
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

		// Délégation de l'association doigts-corps au BodyDetector
		_bodyDetector.AssignPointingFingers(
			pointingFingers,
			_maskSize,
			PaletteOffset,
			BodyPalette.DefaultPalettes.Length,
			_fingerPaletteIndices
		);

		_lastFingerUpdateTime = 0.0;
	}

	/// <summary>
	/// Reçoit le masque de profondeur Kinect et met à jour les corps détectés et échantillons.
	/// </summary>
	public void SetDepthImageMask(Image depthImage)
	{
		if (depthImage == null)
			throw new ArgumentNullException(nameof(depthImage));

		_maskSize = new Vector2I(depthImage.GetWidth(), depthImage.GetHeight());

		// Traitement de segmentation et tracking délégué au module spécialisé
		_bodyDetector.ProcessDepthImage(
			depthImage,
			ClusterStride,
			MaxDepthDiscontinuity,
			MinClusterPixels,
			MaxTrackingDistance,
			MaxMissedFrames,
			PaletteOffset,
			BodyPalette.DefaultPalettes.Length,
			_random,
			_maskPoints
		);

		// Signal si le nombre de corps a évolué
		var currentCount = _bodyDetector.DetectedBodyCount;
		if (currentCount != _lastReportedBodyCount)
		{
			_lastReportedBodyCount = currentCount;
			EmitSignal(SignalName.BodyCountChanged, _lastReportedBodyCount);
		}

		// Redessin de l'overlay de débug si activé
		if (ShowBodyDebug && _debugOverlay != null)
		{
			_debugOverlay.QueueRedraw();
		}
	}

	/// <summary>
	/// Retourne les informations de débug pour l'interface HUD (utilisé par camera_script.gd).
	/// </summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> GetBodiesDebugInfo()
	{
		return _bodyDetector.GetBodiesDebugInfo(BodyPalette.DefaultPalettes);
	}

	// =========================================================================
	// ÉMISSION DE PARTICULES
	// =========================================================================

	/// <summary>
	/// Émet une particule de brume ou un scintillement sur la silhouette du corps.
	/// La forme vient du rendu éthéré et la couleur de la palette du corps détecté.
	/// </summary>
	private void EmitSampledParticle(bool isPointingActive)
	{
		var sample = _maskPoints[_random.RandiRange(0, _maskPoints.Count - 1)];
		var screenPos = MaskToScreen(sample.Position);

		var palettes = BodyPalette.DefaultPalettes;
		var paletteIndex = sample.PaletteIndex % palettes.Length;
		var palette = palettes[paletteIndex];

		// La profondeur pilote à la fois la couleur et la proportion de scintillements.
		var depthSpan = Mathf.Max(_bodyDetector.MaxDepth - _bodyDetector.MinDepth, 0.001f);
		var closeness = Mathf.Clamp((sample.NormalizedDepth - _bodyDetector.MinDepth) / depthSpan, 0.0f, 1.0f);
		var bodyColor = ColorFromDepth ? palette.EvaluateBody(closeness) : palette.ColorNear;
		var sparkleChance = Mathf.Lerp(0.01f, 0.85f, Mathf.Pow(closeness, 1.8f));

		if (_random.Randf() < sparkleChance)
		{
			var sparkleDrift = new Vector2(
				_random.RandfRange(-2.2f, 2.2f),
				_random.RandfRange(-3.0f, 1.2f)
			);
			var sparkleColor = BoostColor(
				bodyColor.Lerp(Colors.White, _random.RandfRange(0.08f, 0.28f)),
				0.98f
			);

			_sparkleParticleSystems[paletteIndex].EmitParticle(
				new Transform2D(0.0f, screenPos + RandomOffset(1.0f)),
				sparkleDrift,
				sparkleColor,
				Colors.White,
				(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
			);
			return;
		}

		// La vague très légère anime la couleur de la brume sans changer l'identité de palette.
		var wave = (float)Math.Sin(_elapsedTime * 1.5 + sample.Position.Y * 0.008f) * 0.12f;
		var harmonicDepth = Mathf.Clamp(closeness + wave, 0.0f, 1.0f);
		var mistBaseColor = ColorFromDepth ? palette.EvaluateBody(harmonicDepth) : bodyColor;
		var mistColor = BoostColor(mistBaseColor, Mathf.Lerp(0.32f, 0.42f, closeness));
		var mistDrift = new Vector2(
			_random.RandfRange(-0.9f, 0.9f),
			_random.RandfRange(-1.5f, 0.4f)
		);

		_mistParticleSystems[paletteIndex].EmitParticle(
			new Transform2D(0.0f, screenPos + RandomOffset(0.8f)),
			mistDrift,
			mistColor,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
		);
	}

	/// <summary>
	/// Émet les particules de tracé le long du mouvement exact des doigts pointés.
	/// </summary>
	private void EmitFingerTrails()
	{
		// Synchronisation de la taille des tampons de positions précédentes et lissées
		if (_prevFingerScreenPos.Count != _currentFingers.Count)
		{
			_prevFingerScreenPos.Clear();
			_smoothedFingerPos.Clear();
			foreach (var finger in _currentFingers)
			{
				var screen = NormalizedToScreen(finger);
				_prevFingerScreenPos.Add(screen);
				_smoothedFingerPos.Add(screen);
			}
			return;
		}

		var palettes = BodyPalette.DefaultPalettes;
		for (var i = 0; i < _currentFingers.Count; i++)
		{
			var rawScreen = NormalizedToScreen(_currentFingers[i]);
			// Lissage exponentiel (EMA) pour absorber les micro-saccades de tracking MediaPipe
			_smoothedFingerPos[i] = _smoothedFingerPos[i].Lerp(rawScreen, 0.58f);
			var currentScreen = _smoothedFingerPos[i];
			var prevScreen = _prevFingerScreenPos[i];
			var distance = prevScreen.DistanceTo(currentScreen);

			var paletteIndex = (i < _fingerPaletteIndices.Count)
				? (_fingerPaletteIndices[i] % palettes.Length)
				: (PaletteOffset % palettes.Length);

			var palette = palettes[paletteIndex];

			if (distance < 500.0f)
			{
				// Pas d'échantillonnage serré pour un ruban néon ultra continu et sans trous
				var steps = Mathf.Max(2, Mathf.CeilToInt(distance / 1.4f));
				for (var s = 0; s <= steps; s++)
				{
					var t = (float)s / steps;
					var center = prevScreen.Lerp(currentScreen, t);

					// Cœur lumineux fluide au bout du doigt uniquement
					EmitSingleTrailCoreParticle(center, paletteIndex, palette);
				}
			}
			else
			{
				for (var s = 0; s < 6; s++)
				{
					EmitSingleTrailCoreParticle(currentScreen, paletteIndex, palette);
				}
			}

			_prevFingerScreenPos[i] = currentScreen;
		}
	}

	/// <summary>
	/// Émet une particule de cœur fluide lumineux pour le tracé de dessin.
	/// </summary>
	private void EmitSingleTrailCoreParticle(Vector2 basePosition, int paletteIndex, BodyPalette palette)
	{
		var offset = new Vector2(
			_random.RandfRange(-0.8f, 0.8f),
			_random.RandfRange(-0.8f, 0.8f)
		);
		var drift = new Vector2(
			_random.RandfRange(-0.15f, 0.15f),
			_random.RandfRange(-0.15f, 0.15f)
		);

		var color = BoostColor(palette.EvaluateTrail(_random.Randf()), 0.96f);

		_trailCoreParticleSystems[paletteIndex].EmitParticle(
			new Transform2D(0.0f, basePosition + offset),
			drift,
			color,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
		);
	}

	/// <summary>
	/// Émet une particule de poussière d'étoiles scintillantes avec rotation et dispersion.
	/// </summary>
	private void EmitSingleTrailSparkleParticle(Vector2 basePosition, int paletteIndex, BodyPalette palette)
	{
		var angle = _random.RandfRange(0.0f, Mathf.Tau);
		var radius = _random.RandfRange(1.0f, 10.0f);
		var offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;

		var drift = new Vector2(
			_random.RandfRange(-1.2f, 1.2f),
			_random.RandfRange(-2.2f, 0.4f)
		);

		var sparkleColor = BoostColor(
			palette.EvaluateTrail(_random.Randf()).Lerp(Colors.White, _random.RandfRange(0.30f, 0.75f)),
			0.98f
		);

		_trailSparkleParticleSystems[paletteIndex].EmitParticle(
			new Transform2D(_random.RandfRange(0.0f, Mathf.Tau), basePosition + offset),
			drift,
			sparkleColor,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
		);
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
			color.R * EtherealGlowIntensity,
			color.G * EtherealGlowIntensity,
			color.B * EtherealGlowIntensity,
			alpha
		);
	}

	// =========================================================================
	// CONVERSIONS DE COORDONNÉES
	// =========================================================================

	private Vector2 NormalizedToScreen(Vector2 normalized)
	{
		return MaskToScreen(new Vector2(normalized.X * _maskSize.X, normalized.Y * _maskSize.Y));
	}

	/// <summary>
	/// Convertit un point de l'espace masque (résolution caméra Kinect) vers l'espace écran du viewport.
	/// </summary>
	public Vector2 MaskToScreen(Vector2 maskPoint)
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

	// =========================================================================
	// FABRIQUE DE SYSTÈMES ET MATÉRIAUX GPU
	// =========================================================================

	/// <summary>
	/// Crée le système de brume douce d'une palette de corps.
	/// </summary>
	private GpuParticles2D CreateMistParticleSystem(BodyPalette palette)
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.18f, 0.65f));
		alphaCurve.AddPoint(new Vector2(0.65f, 0.55f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.35f));
		scaleCurve.AddPoint(new Vector2(0.3f, 0.6f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.25f));

		var colorRamp = new Gradient();
		colorRamp.SetColor(0, Opaque(palette.ColorFar));
		colorRamp.AddPoint(0.45f, Opaque(palette.ColorNear));
		colorRamp.AddPoint(1.0f, Opaque(palette.ColorFar));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, -1.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 1.5f,
			DampingMin = 2.0f,
			DampingMax = 4.0f,
			ScaleMin = 0.22f,
			ScaleMax = 0.48f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = colorRamp },
			Color = Opaque(palette.ColorNear),
		};

		return new GpuParticles2D
		{
			Amount = MaxMistParticles,
			Lifetime = MistLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateMistTexture(14),
			ProcessMaterial = processMat,
			Material = CreateCanvasMaterial(),
			VisibilityRect = new Rect2(-100, -100, 10000, 10000),
		};
	}

	/// <summary>
	/// Crée le système de scintillements en forme d'étoile d'une palette de corps.
	/// </summary>
	private GpuParticles2D CreateSparkleParticleSystem(BodyPalette palette)
	{
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.10f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.45f, 0.95f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.2f));
		scaleCurve.AddPoint(new Vector2(0.20f, 1.0f));
		scaleCurve.AddPoint(new Vector2(0.60f, 0.7f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.1f));

		var colorRamp = new Gradient();
		colorRamp.SetColor(0, Opaque(palette.ColorNear));
		colorRamp.AddPoint(0.45f, Opaque(palette.ColorNear.Lerp(Colors.White, 0.22f)));
		colorRamp.AddPoint(1.0f, Opaque(palette.ColorFar));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, -0.8f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 3.0f,
			DampingMin = 3.0f,
			DampingMax = 6.0f,
			ScaleMin = 0.28f,
			ScaleMax = 0.65f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = colorRamp },
			Color = Opaque(palette.ColorNear),
		};

		return new GpuParticles2D
		{
			Amount = MaxSparkleParticles,
			Lifetime = SparkleLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSparkleTexture(16),
			ProcessMaterial = processMat,
			Material = CreateCanvasMaterial(),
			VisibilityRect = new Rect2(-100, -100, 10000, 10000),
		};
	}

	/// <summary>
	/// Crée le système de cœur fluide lumineux du tracé de dessin.
	/// </summary>
	private GpuParticles2D CreateTrailCoreParticleSystem(BodyPalette palette)
	{
		var gradient = new Gradient();
		gradient.SetColor(0, WithAlpha(palette.TrailStart, 0.98f));
		gradient.AddPoint(0.45f, WithAlpha(palette.TrailStart.Lerp(palette.TrailEnd, 0.5f), 0.92f));
		gradient.AddPoint(0.85f, WithAlpha(palette.TrailEnd, 0.60f));
		gradient.AddPoint(1.0f, WithAlpha(palette.TrailEnd, 0.0f));

		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.05f, 0.95f));
		alphaCurve.AddPoint(new Vector2(0.80f, 0.85f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.45f));
		scaleCurve.AddPoint(new Vector2(0.12f, 0.85f));
		scaleCurve.AddPoint(new Vector2(0.82f, 0.75f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.15f));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, 0.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.6f,
			DampingMin = 2.0f,
			DampingMax = 4.0f,
			ScaleMin = 0.35f,
			ScaleMax = 0.70f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = gradient },
			Color = Opaque(palette.TrailStart),
		};

		return new GpuParticles2D
		{
			Amount = MaxTrailParticles,
			Lifetime = TrailLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateTrailGlowTexture(24),
			ProcessMaterial = processMat,
			Material = CreateCanvasMaterial(),
			VisibilityRect = new Rect2(-200, -200, 10000, 10000),
		};
	}

	/// <summary>
	/// Crée le système de poussière d'étoiles scintillantes du tracé de dessin.
	/// </summary>
	private GpuParticles2D CreateTrailSparkleParticleSystem(BodyPalette palette)
	{
		var gradient = new Gradient();
		gradient.SetColor(0, WithAlpha(palette.TrailStart.Lerp(Colors.White, 0.40f), 1.0f));
		gradient.AddPoint(0.50f, WithAlpha(palette.TrailEnd, 0.90f));
		gradient.AddPoint(1.0f, WithAlpha(palette.TrailEnd, 0.0f));

		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		alphaCurve.AddPoint(new Vector2(0.08f, 1.0f));
		alphaCurve.AddPoint(new Vector2(0.55f, 0.90f));
		alphaCurve.AddPoint(new Vector2(0.88f, 0.55f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.AddPoint(new Vector2(0.0f, 0.20f));
		scaleCurve.AddPoint(new Vector2(0.18f, 1.0f));
		scaleCurve.AddPoint(new Vector2(0.65f, 0.70f));
		scaleCurve.AddPoint(new Vector2(1.0f, 0.08f));

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			AngleMin = 0.0f,
			AngleMax = 360.0f,
			AngularVelocityMin = -45.0f,
			AngularVelocityMax = 45.0f,
			Gravity = new Vector3(0.0f, -0.30f, 0.0f),
			InitialVelocityMin = 0.2f,
			InitialVelocityMax = 2.5f,
			DampingMin = 2.0f,
			DampingMax = 4.5f,
			ScaleMin = 0.20f,
			ScaleMax = 0.60f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = gradient },
			Color = Opaque(palette.TrailStart),
		};

		return new GpuParticles2D
		{
			Amount = MaxTrailParticles,
			Lifetime = TrailLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateTrailSparkleTexture(18),
			ProcessMaterial = processMat,
			Material = CreateCanvasMaterial(),
			VisibilityRect = new Rect2(-200, -200, 10000, 10000),
		};
	}

	private static ImageTexture CreateTrailGlowTexture(int size)
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

				var core = Mathf.Pow(Mathf.Clamp(1.0f - distance, 0.0f, 1.0f), 2.2f);
				var halo = Mathf.Pow(Mathf.Clamp(1.0f - distance, 0.0f, 1.0f), 0.7f) * 0.35f;
				var alpha = Mathf.Clamp(core * 0.85f + halo, 0.0f, 1.0f);
				image.SetPixel(x, y, new Color(1, 1, 1, alpha));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}

	private static ImageTexture CreateTrailSparkleTexture(int size)
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
				var core = Mathf.Pow(Mathf.Clamp(1.0f - distance * 2.0f, 0.0f, 1.0f), 2.5f);
				var hRay = Mathf.Pow(Mathf.Clamp(1.0f - nx, 0.0f, 1.0f), 1.4f)
					* Mathf.Pow(Mathf.Clamp(1.0f - ny * 3.8f, 0.0f, 1.0f), 2.2f);
				var vRay = Mathf.Pow(Mathf.Clamp(1.0f - ny, 0.0f, 1.0f), 1.4f)
					* Mathf.Pow(Mathf.Clamp(1.0f - nx * 3.8f, 0.0f, 1.0f), 2.2f);
				var alpha = Mathf.Clamp(core * 1.4f + hRay * 0.85f + vRay * 0.85f, 0.0f, 1.0f);
				image.SetPixel(x, y, new Color(1, 1, 1, alpha));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}

	private CanvasItemMaterial? CreateCanvasMaterial()
	{
		if (!AdditiveBlending)
			return null;

		return new CanvasItemMaterial
		{
			BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
			LightMode = CanvasItemMaterial.LightModeEnum.Unshaded,
		};
	}

	private static Color Opaque(Color color)
	{
		return WithAlpha(color, 1.0f);
	}

	private static Color WithAlpha(Color color, float alpha)
	{
		return new Color(color.R, color.G, color.B, alpha);
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
				var hRay = Mathf.Pow(Mathf.Clamp(1.0f - nx, 0.0f, 1.0f), 1.2f)
					* Mathf.Pow(Mathf.Clamp(1.0f - ny * 3.5f, 0.0f, 1.0f), 2.0f);
				var vRay = Mathf.Pow(Mathf.Clamp(1.0f - ny, 0.0f, 1.0f), 1.2f)
					* Mathf.Pow(Mathf.Clamp(1.0f - nx * 3.5f, 0.0f, 1.0f), 2.0f);
				var alpha = Mathf.Clamp(core * 1.3f + hRay * 0.7f + vRay * 0.7f, 0.0f, 1.0f);
				image.SetPixel(x, y, new Color(1, 1, 1, alpha));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}
}

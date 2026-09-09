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

	/// <summary>Durée de vie (secondes) des particules de la silhouette du corps.</summary>
	[Export] public float BodyLifetime { get; set; } = 0.22f;

	[ExportGroup("Débits et Limites")]
	/// <summary>Nombre de particules de silhouette émises par seconde.</summary>
	[Export] public int ParticlesPerSecond { get; set; } = 3600;

	/// <summary>Capacité maximale du pool de particules de corps par système.</summary>
	[Export] public int MaxBodyParticles { get; set; } = 6000;

	/// <summary>Capacité maximale du pool de particules de tracé par système.</summary>
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

	// 5 systèmes GPU dédiés pour les corps et 5 pour les tracés (un par palette)
	private readonly GpuParticles2D[] _bodyParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _trailParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];

	private BodyDebugOverlay _debugOverlay = null!;
	private Vector2I _maskSize = new(640, 480);
	private float _emissionRemainder;
	private bool _isPointingUp;
	private double _lastFingerUpdateTime;
	private int _lastReportedBodyCount = -1;

	// =========================================================================
	// CYCLE DE VIE GODOT
	// =========================================================================

	public override void _Ready()
	{
		_random.Randomize();

		// Initialisation des 5 systèmes GPU de corps et 5 systèmes de tracés
		var palettes = BodyPalette.DefaultPalettes;
		for (var i = 0; i < palettes.Length; i++)
		{
			_bodyParticleSystems[i] = CreateBodyParticleSystem(palettes[i]);
			AddChild(_bodyParticleSystems[i]);

			_trailParticleSystems[i] = CreateTrailParticleSystem(palettes[i]);
			AddChild(_trailParticleSystems[i]);
		}

		// Initialisation de l'overlay de débug séparé
		_debugOverlay = new BodyDebugOverlay();
		_debugOverlay.Initialize(this);
		_debugOverlay.IsVisibleInGame = ShowBodyDebug;
		AddChild(_debugOverlay);
	}

	public override void _Process(double delta)
	{
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
	/// Émet une particule échantillonnée sur la silhouette du corps,
	/// dans le système GPU correspondant à la palette du corps détecté.
	/// </summary>
	private void EmitSampledParticle(bool isPointingActive)
	{
		var sample = _maskPoints[_random.RandiRange(0, _maskPoints.Count - 1)];
		var screenPos = MaskToScreen(sample.Position);

		// Vérifier si l'échantillon est situé à proximité immédiate d'un doigt pointé
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
		var palettes = BodyPalette.DefaultPalettes;
		var paletteIndex = sample.PaletteIndex % palettes.Length;
		var palette = palettes[paletteIndex];

		// Interpolation de la couleur selon la distance/profondeur Z mesurée
		var depthSpan = Mathf.Max(_bodyDetector.MaxDepth - _bodyDetector.MinDepth, 0.001f);
		var depthT = Mathf.Clamp((sample.NormalizedDepth - _bodyDetector.MinDepth) / depthSpan, 0.0f, 1.0f);
		var color = ColorFromDepth ? palette.EvaluateBody(depthT) : palette.ColorNear;

		// Sélection du système GPU dédié à cette palette
		var targetSystem = isNearPointingHand
			? _trailParticleSystems[paletteIndex]
			: _bodyParticleSystems[paletteIndex];

		var finalDrift = isNearPointingHand ? drift * 0.4f : drift;
		if (isNearPointingHand)
		{
			color = palette.EvaluateTrail(_random.Randf());
		}

		targetSystem.EmitParticle(
			new Transform2D(0.0f, screenPos),
			finalDrift,
			color,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
		);
	}

	/// <summary>
	/// Émet les particules de tracé le long du mouvement des doigts pointés.
	/// </summary>
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

		var palettes = BodyPalette.DefaultPalettes;
		for (var i = 0; i < _currentFingers.Count; i++)
		{
			var currentScreen = NormalizedToScreen(_currentFingers[i]);
			var prevScreen = _prevFingerScreenPos[i];
			var distance = prevScreen.DistanceTo(currentScreen);
			var paletteIndex = (i < _fingerPaletteIndices.Count)
				? (_fingerPaletteIndices[i] % palettes.Length)
				: (PaletteOffset % palettes.Length);

			var targetSystem = _trailParticleSystems[paletteIndex];
			var palette = palettes[paletteIndex];

			if (distance < 500.0f)
			{
				// Interpolation linéaire pour que le tracé reste continu même lors de mouvements vifs
				var steps = Mathf.Max(2, Mathf.CeilToInt(distance / 3.0f));
				for (var s = 0; s <= steps; s++)
				{
					var t = (float)s / steps;
					var center = prevScreen.Lerp(currentScreen, t);
					EmitSingleTrailParticle(center, targetSystem, palette);
				}
			}
			else
			{
				for (var s = 0; s < 4; s++)
				{
					EmitSingleTrailParticle(currentScreen, targetSystem, palette);
				}
			}

			_prevFingerScreenPos[i] = currentScreen;
		}
	}

	/// <summary>
	/// Émet une particule de tracé unique dans le système GPU spécifié.
	/// </summary>
	private void EmitSingleTrailParticle(Vector2 basePosition, GpuParticles2D targetSystem, BodyPalette palette)
	{
		var offset = new Vector2(
			_random.RandfRange(-6.0f, 6.0f),
			_random.RandfRange(-6.0f, 6.0f)
		);
		var drift = new Vector2(
			_random.RandfRange(-5.0f, 5.0f),
			_random.RandfRange(-6.0f, 4.0f)
		);

		var color = palette.EvaluateTrail(_random.Randf());

		targetSystem.EmitParticle(
			new Transform2D(0.0f, basePosition + offset),
			drift,
			color,
			Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity | GpuParticles2D.EmitFlags.Color)
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
	/// Crée un système GPU dédié pour une palette de corps, avec son dégradé ColorRamp configuré.
	/// </summary>
	private GpuParticles2D CreateBodyParticleSystem(BodyPalette palette)
	{
		var gradient = new Gradient();
		gradient.SetColor(0, new Color(palette.ColorFar.R, palette.ColorFar.G, palette.ColorFar.B, 0.70f));
		gradient.AddPoint(0.40f, new Color(palette.ColorNear.R, palette.ColorNear.G, palette.ColorNear.B, 0.95f));
		gradient.AddPoint(0.85f, new Color(palette.ColorFar.R, palette.ColorFar.G, palette.ColorFar.B, 0.50f));
		gradient.AddPoint(1.0f, new Color(palette.ColorFar.R, palette.ColorFar.G, palette.ColorFar.B, 0.0f));

		var colorRamp = new GradientTexture1D
		{
			Gradient = gradient,
		};

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, -3.0f, 0.0f), // Légère dérive ascendante (aura)
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 4.0f,
			DampingMin = 2.0f,
			DampingMax = 5.0f,
			ScaleMin = 0.6f,
			ScaleMax = 1.35f,
			ColorRamp = colorRamp,
			Color = palette.ColorNear,
		};

		var canvasMat = AdditiveBlending
			? new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add }
			: null;

		return new GpuParticles2D
		{
			Amount = MaxBodyParticles,
			Lifetime = BodyLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSoftParticleTexture(20),
			ProcessMaterial = processMat,
			Material = canvasMat,
			VisibilityRect = new Rect2(-100, -100, 10000, 10000),
		};
	}

	/// <summary>
	/// Crée un système GPU dédié pour les tracés de dessin d'une palette.
	/// </summary>
	private GpuParticles2D CreateTrailParticleSystem(BodyPalette palette)
	{
		var gradient = new Gradient();
		gradient.SetColor(0, new Color(palette.TrailStart.R, palette.TrailStart.G, palette.TrailStart.B, 1.0f));
		gradient.AddPoint(0.65f, new Color(palette.TrailEnd.R, palette.TrailEnd.G, palette.TrailEnd.B, 0.9f));
		gradient.AddPoint(1.0f, new Color(palette.TrailEnd.R, palette.TrailEnd.G, palette.TrailEnd.B, 0.0f));

		var colorRamp = new GradientTexture1D
		{
			Gradient = gradient,
		};

		var processMat = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			Gravity = new Vector3(0.0f, 4.0f, 0.0f),
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 6.0f,
			DampingMin = 2.0f,
			DampingMax = 5.0f,
			ScaleMin = 0.7f,
			ScaleMax = 1.6f,
			ColorRamp = colorRamp,
			Color = palette.TrailStart,
		};

		var canvasMat = AdditiveBlending
			? new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add }
			: null;

		return new GpuParticles2D
		{
			Amount = MaxTrailParticles,
			Lifetime = TrailLifetime,
			LocalCoords = false,
			Emitting = false,
			Texture = CreateSoftParticleTexture(22),
			ProcessMaterial = processMat,
			Material = canvasMat,
			VisibilityRect = new Rect2(-200, -200, 10000, 10000),
		};
	}

	/// <summary>
	/// Génère une texture circulaire douce avec atténuation quadratique pour un rendu lumineux diffus.
	/// </summary>
	private static ImageTexture CreateSoftParticleTexture(int size)
	{
		var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		var center = (size - 1) * 0.5f;
		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var distance = new Vector2(x - center, y - center).Length() / center;
				var falloff = Mathf.Clamp(1.0f - distance, 0.0f, 1.0f);
				var alpha = falloff * falloff;
				image.SetPixel(x, y, new Color(1, 1, 1, alpha * 0.75f));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}
}

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

	/// <summary>Émis lorsqu'un tir de pistolet est effectué.</summary>
	[Signal] public delegate void GunShotFiredEventHandler(int trackId, Vector2 screenPos, Vector2 direction);

	/// <summary>Émis lorsque l'easter egg du cri de Wilhelm est déclenché.</summary>
	[Signal] public delegate void WilhelmEasterEggTriggeredEventHandler(Vector2 screenPos);

	[ExportGroup("Traînée multicolore")]
	[Export(PropertyHint.Range, "0.1,3,0.05")] public float OutwardLifetime { get; set; } = 0.75f;
	[Export] public int OutwardParticlesPerSecond { get; set; } = 8000;
	[Export] public int MaxOutwardParticles { get; set; } = 18000;
	// Initial fall speed. It is depth-linked and shared by every body sample.
	[Export] public float OutwardSpeed { get; set; } = 8.0f;
	[Export] public float OutwardSize { get; set; } = 100.0f;
	[Export] public float OutwardGravity { get; set; } = 300.0f;
	[Export(PropertyHint.Range, "0,10,0.1")] public float OutwardDamping { get; set; } = 10.0f;
	// Optical density, not RGB amplification: zero hides smoke without whitening its colors.
	[Export(PropertyHint.Range, "0,4,0.05,or_greater")] public float OutwardIntensity { get; set; } = 1.65f;
	// These settings also update the live material in the Remote Inspector.
	[Export] public bool OutwardTurbulenceEnabled { get; set; } = true;
	[Export(PropertyHint.Range, "0,1,0.01")] public float OutwardTurbulenceInfluence { get; set; } = 0.16f;
	// Godot's scale is nonlinear: 6 produces pixel-sized noise in 2D; 9.8 gives broad curls.
	[Export(PropertyHint.Range, "0,9.99,0.01")] public float OutwardTurbulenceScale { get; set; } = 8.0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float OutwardTurbulenceEvolution { get; set; } = 0.15f;
	[Export] public float DepthFarThreshold { get; set; } = 0.67f;
	[Export] public float DepthNearThreshold { get; set; } = 0.88f;

	// =========================================================================
	// PARAMÈTRES EXPORTÉS (Inspecteur Godot)
	// =========================================================================

	[ExportGroup("Pistolet Particules")]
	/// <summary>Intervalle entre chaque coup en tir automatique continu (secondes).</summary>
	[Export] public float GunFireInterval
	{
		get => _gunParticleManager.FireInterval;
		set => _gunParticleManager.FireInterval = value;
	}

	/// <summary>Vélocité minimale des projectiles de tir.</summary>
	[Export] public float GunProjectileSpeedMin
	{
		get => _gunParticleManager.ProjectileSpeedMin;
		set => _gunParticleManager.ProjectileSpeedMin = value;
	}

	/// <summary>Vélocité maximale des projectiles de tir.</summary>
	[Export] public float GunProjectileSpeedMax
	{
		get => _gunParticleManager.ProjectileSpeedMax;
		set => _gunParticleManager.ProjectileSpeedMax = value;
	}

	[ExportGroup("Durées de vie")]
	/// <summary>Durée de vie (secondes) des particules de tracé de dessin persistant.</summary>
	[Export] public float TrailLifetime { get; set; } = 8.0f;

	/// <summary>Durée de vie (secondes) de la brume qui dessine la silhouette.</summary>
	[Export] public float MistLifetime { get; set; } = 0.38f;

	/// <summary>Durée de vie (secondes) des scintillements de la silhouette.</summary>
	[Export] public float SparkleLifetime { get; set; } = 0.22f;

	[ExportGroup("Débits et Limites")]
	/// <summary>Nombre de particules de silhouette émises par seconde.</summary>
	[Export] public int ParticlesPerSecond { get; set; } = 100000;

	/// <summary>Capacité maximale du pool de brume pour une palette.</summary>
	[Export] public int MaxMistParticles { get; set; } = 150000;

	/// <summary>Capacité maximale du pool de scintillements pour une palette.</summary>
	[Export] public int MaxSparkleParticles { get; set; } = 120000;

	/// <summary>Capacité maximale du pool de tracé pour une palette.</summary>
	[Export] public int MaxTrailParticles { get; set; } = 25000;

	[ExportGroup("Lucioles ambiantes")]
	/// <summary>Nombre de lucioles permanentes affichées dans le fond.</summary>
	[Export] public int AmbientParticleCount { get; set; } = 2000;

	/// <summary>Vitesse de dérive naturelle des lucioles en pixels par seconde.</summary>
	[Export] public float AmbientParticleSpeed { get; set; } = 12.0f;

	/// <summary>Distance d'influence de la silhouette autour de son masque, en pixels écran.</summary>
	[Export] public float AmbientInfluenceRadius { get; set; } = 48.0f;

	/// <summary>Force avec laquelle le contour de la silhouette repousse les lucioles.</summary>
	[Export] public float AmbientRepulsionStrength { get; set; } = 150.0f;

	/// <summary>Proportion du mouvement corporel transmise aux lucioles proches.</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float AmbientBodyWindInfluence { get; set; } = 0.25f;

	/// <summary>Diamètre minimal d'une luciole en pixels.</summary>
	[Export] public float AmbientParticleSizeMin { get; set; } = 3.0f;

	/// <summary>Diamètre maximal d'une luciole en pixels.</summary>
	[Export] public float AmbientParticleSizeMax { get; set; } = 8.0f;

	/// <summary>Multiplicateur de vitesse du scintillement.</summary>
	[Export] public float AmbientTwinkleSpeed { get; set; } = 1.0f;

	/// <summary>Force maximale de rappel vers la répartition uniforme.</summary>
	[Export] public float AmbientReturnStrength { get; set; } = 0.22f;

	/// <summary>Part du rappel conservée lorsqu'au moins une silhouette est visible.</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float AmbientReturnWhileBodies { get; set; } = 0.35f;

	/// <summary>Amplitude du mouvement libre autour de l'ancre de chaque luciole, en pixels.</summary>
	[Export] public float AmbientAnchorWanderRadius { get; set; } = 18.0f;

	/// <summary>Délai avant le retour aux ancres après la disparition de la dernière silhouette.</summary>
	[Export] public float AmbientReturnDelay { get; set; } = 0.4f;

	/// <summary>Opacité maximale des lucioles.</summary>
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float AmbientParticleOpacity { get; set; } = 0.6f;

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
	[Export] public float MaxDepthDiscontinuity { get; set; } = 0.30f;

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

	private GpuParticles2D _outwardParticleSystem = null!;
	private float _outwardEmissionRemainder;
	private float _meanBodyDepth;
	private Transform2D _maskToScreen = Transform2D.Identity;
	private Transform2D _bodyToScreen = Transform2D.Identity;
	private OutwardSettings? _appliedOutwardSettings;
	private bool _demoBodyEnabled;
	private float _demoBodyOffsetX;
	private float _demoBodyScale = 1.0f;
	private float _demoBodyDepth;

	private readonly BodyDetector _bodyDetector = new();
	private readonly List<MaskSample> _maskPoints = new();
	private readonly List<Vector2> _currentFingers = new();
	private readonly List<int> _fingerPaletteIndices = new();
	private readonly List<Vector2> _prevFingerScreenPos = new();
	private readonly RandomNumberGenerator _random = new();

	// Pipeline visuel brume/scintillement pour le corps,
	// pipeline double pour le tracé de dessin persistant,
	// et délégation complète du geste pistolet à GunParticleManager.
	private readonly GpuParticles2D[] _mistParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _sparkleParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _trailCoreParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _trailSparkleParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];

	private readonly GunParticleManager _gunParticleManager = new();
	private readonly List<Vector2> _smoothedFingerPos = new();

	private AmbientFireflies _ambientFireflies = null!;
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

	private float _NormalizeDepth(float depth)
	{
		var depthSpan = Mathf.Max(_bodyDetector.MaxDepth - _bodyDetector.MinDepth, 0.001f);

		return Mathf.Clamp((depth - _bodyDetector.MinDepth) / depthSpan, 0.0f, 1.0f);
	}

	public override void _Ready()
	{
		_random.Randomize();
		_outwardParticleSystem = CreateOutwardParticleSystem();
		AddChild(_outwardParticleSystem);
		ApplyOutwardSettings();
		UpdateMaskMapping();

		// Le premier enfant du CanvasLayer reste derrière toutes les particules corporelles.
		_ambientFireflies = new AmbientFireflies
		{
			ParticleCount = AmbientParticleCount,
			AmbientSpeed = AmbientParticleSpeed,
			InfluenceRadius = AmbientInfluenceRadius,
			RepulsionStrength = AmbientRepulsionStrength,
			BodyWindInfluence = AmbientBodyWindInfluence,
			ParticleSizeMin = AmbientParticleSizeMin,
			ParticleSizeMax = AmbientParticleSizeMax,
			TwinkleSpeed = AmbientTwinkleSpeed,
			ReturnStrength = AmbientReturnStrength,
			ReturnWhileBodies = AmbientReturnWhileBodies,
			AnchorWanderRadius = AmbientAnchorWanderRadius,
			ReturnDelay = AmbientReturnDelay,
			AmbientOpacity = AmbientParticleOpacity,
			PreserveAspectRatio = PreserveAspectRatio,
		};
		AddChild(_ambientFireflies);
		_ambientFireflies.Initialize(AdditiveBlending);

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

		// Initialisation du gestionnaire de particules pistolet
		_gunParticleManager.Initialize(
			this,
			palettes,
			CreateCanvasMaterial(),
			CreateTrailGlowTexture(24),
			CreateSparkleTexture(16),
			EtherealGlowIntensity
		);

		// Initialisation de l'overlay de débug séparé
		_debugOverlay = new BodyDebugOverlay();
		_debugOverlay.Initialize(this);
		_debugOverlay.IsVisibleInGame = ShowBodyDebug;
		AddChild(_debugOverlay);
	}

	public override void _Process(double delta)
	{
		ApplyOutwardSettings();
		UpdateMaskMapping();
		// One global intensity based on the body's average depth (or the demo depth).
		var closeness = GetCloseness(_demoBodyEnabled ? _demoBodyDepth : _meanBodyDepth);
		var outwardIntensity = Mathf.Max(0.0f, OutwardIntensity) * Mathf.Lerp(0.4f, 1.0f, closeness);
		// Native alpha blending: intensity controls opacity only, not RGB brightness.
		_outwardParticleSystem.SelfModulate = new Color(1.0f, 1.0f, 1.0f,
			1.0f - Mathf.Exp(-outwardIntensity));
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

		// 1.5. Émission des tirs de particules pour le geste pistolet (Finger Gun)
		_gunParticleManager.ProcessGunFirings(
			_elapsedTime,
			NormalizedToScreen,
			GetPaletteIndexForTrack,
			(trackId, pos, dir) => EmitSignal(SignalName.GunShotFired, trackId, pos, dir)
		);

		// 2. Émission des particules de silhouette corporelle
		if (_maskPoints.Count == 0)
		{
			_emissionRemainder = 0.0f;
			_outwardEmissionRemainder = 0.0f;
			return;
		}

		// Do not catch up an entire stalled frame with a burst of particles.
		var emissionDelta = (float)Math.Clamp(delta, 0.0, 0.1);
		_emissionRemainder += Mathf.Max(0, ParticlesPerSecond) * emissionDelta;
		var particleCount = Mathf.FloorToInt(_emissionRemainder);
		_emissionRemainder -= particleCount;

		for (var i = 0; i < particleCount; i++)
		{
			EmitSampledParticle(isPointingActive);
		}

		// The coloured trail is sourced across the entire silhouette, rather than its
		// edge.  Its rate still adapts to body coverage without exploding for a large mask.
		var coverage = Mathf.Clamp(Mathf.Sqrt(_maskPoints.Count / 5000.0f), 0.5f, 1.5f)
			* Mathf.Pow(80.0f / Mathf.Max(16.0f, OutwardSize), 2.0f);
		var requestedRate = Mathf.Max(0, OutwardParticlesPerSecond) * coverage;
		var safeRate = _outwardParticleSystem.Amount * 0.9f / (float)_outwardParticleSystem.Lifetime;
		_outwardEmissionRemainder += Mathf.Min(requestedRate, safeRate) * emissionDelta;
		var outwardParticleCount = Mathf.FloorToInt(_outwardEmissionRemainder);
		_outwardEmissionRemainder -= outwardParticleCount;
		for (var i = 0; i < outwardParticleCount; i++)
			EmitOutwardParticle();
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
	/// Met à jour l'état des mains en posture de pistolet (FINGER_GUN) avec coordonnées et vecteur de visée.
	/// </summary>
	public void UpdateGunState(Godot.Collections.Array<Godot.Collections.Dictionary> gunDetections)
	{
		_gunParticleManager.UpdateGunState(gunDetections);
	}

	/// <summary>
	/// Émet une salve de tir de particules depuis le bout des doigts dans la direction visée (gauche ou droite).
	/// </summary>
	public void EmitGunShot(Vector2 normalizedAnchor, Vector2 normalizedDirection, int trackId)
	{
		_gunParticleManager.EmitGunShot(
			normalizedAnchor,
			normalizedDirection,
			trackId,
			NormalizedToScreen,
			GetPaletteIndexForTrack,
			(tid, screenPos, dir) => EmitSignal(SignalName.GunShotFired, tid, screenPos, dir)
		);
	}

	/// <summary>
	/// Émet une explosion de particules pour l'easter egg du Cri de Wilhelm.
	/// </summary>
	public void EmitWilhelmEasterEgg(Vector2 normalizedAnchor)
	{
		var screenPos = NormalizedToScreen(normalizedAnchor);
		_gunParticleManager.EmitWilhelmBurst(screenPos);
		EmitSignal(SignalName.WilhelmEasterEggTriggered, screenPos);
	}

	private int GetPaletteIndexForTrack(int trackId)
	{
		for (var b = 0; b < _bodyDetector.TrackedBodies.Count; b++)
		{
			if (_bodyDetector.TrackedBodies[b].Id == trackId)
			{
				return _bodyDetector.TrackedBodies[b].PaletteIndex;
			}
		}
		return PaletteOffset;
	}

	/// <summary>
	/// Reçoit le masque de profondeur Kinect et met à jour les corps détectés et échantillons.
	/// </summary>
	public void SetDepthImageMask(Image depthImage)
	{
		if (depthImage == null)
			throw new ArgumentNullException(nameof(depthImage));

		_maskSize = new Vector2I(depthImage.GetWidth(), depthImage.GetHeight());
		UpdateMaskMapping();

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
		_ambientFireflies.UpdateSilhouette(
			_maskPoints,
			_bodyDetector.TrackedBodies,
			_maskSize
		);
		UpdateOutwardDepth();

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
		var screenPos = BodyMaskToScreen(sample.Position);

		var palettes = BodyPalette.DefaultPalettes;
		var paletteIndex = sample.PaletteIndex % palettes.Length;
		var palette = palettes[paletteIndex];

		// Keep main's calibrated live-camera depth, while the demo keeps its explicit depth.
		var closeness = _demoBodyEnabled ? GetCloseness(_demoBodyDepth)
			: _NormalizeDepth(sample.NormalizedDepth);
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

			_sparkleParticleSystems[paletteIndex].ProcessMaterial.Set("color", sparkleColor);

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
		
		_mistParticleSystems[paletteIndex].ProcessMaterial.Set("color", mistColor);

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

		_trailCoreParticleSystems[paletteIndex].ProcessMaterial.Set("color", color);

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

		_trailSparkleParticleSystems[paletteIndex].ProcessMaterial.Set("color", sparkleColor);

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
		return _maskToScreen * maskPoint;
	}

	private void UpdateMaskMapping()
	{
		var viewport = GetViewport().GetVisibleRect().Size;
		var scaleX = viewport.X / Mathf.Max(_maskSize.X, 1);
		var scaleY = viewport.Y / Mathf.Max(_maskSize.Y, 1);
		var scale = PreserveAspectRatio ? Mathf.Min(scaleX, scaleY) : 1.0f;

		var screenScale = PreserveAspectRatio ? Vector2.One * scale : new Vector2(scaleX, scaleY);
		var center = new Vector2(_maskSize.X, _maskSize.Y) * 0.5f;
		_maskToScreen = new Transform2D(new Vector2(screenScale.X, 0), new Vector2(0, screenScale.Y),
			(viewport - center * 2.0f * screenScale) * 0.5f);
		_bodyToScreen = _demoBodyEnabled
			? _maskToScreen * new Transform2D(Vector2.Right * _demoBodyScale, Vector2.Down * _demoBodyScale,
				center * (1.0f - _demoBodyScale) + new Vector2(_demoBodyOffsetX, 0))
			: _maskToScreen;
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
		colorRamp.SetColor(0, Colors.White);
		colorRamp.AddPoint(0.25f, Colors.White);
		colorRamp.AddPoint(0.75f, new Color(0.65f, 0.65f, 0.65f, 1.0f));
		colorRamp.AddPoint(1.0f, new Color(0.15f, 0.15f, 0.15f, 1.0f));

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
			Texture = CreateMistTexture(18),
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
		colorRamp.SetColor(0, Colors.White);
		colorRamp.AddPoint(0.25f, Colors.White);
		colorRamp.AddPoint(0.75f, new Color(0.65f, 0.65f, 0.65f, 1.0f));
		colorRamp.AddPoint(1.0f, new Color(0.15f, 0.15f, 0.15f, 1.0f));

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
			Texture = CreateSparkleTexture(20),
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

	private void UpdateOutwardDepth()
	{
		// BodyDetector already owns the sampled analogue mask. Reuse those samples:
		// no RGB copy and no second edge cache are needed for the coloured trail.
		// Keep the previous value on an empty mask so existing particles fade naturally.
		if (_maskPoints.Count == 0)
			return;

		var depthSum = 0.0f;
		foreach (var sample in _maskPoints)
			depthSum += sample.NormalizedDepth;
		_meanBodyDepth = depthSum / _maskPoints.Count;
	}

	public void SetDemoBodyTransform(bool enabled, float offsetX, float scale, float depth)
	{
		if (_demoBodyEnabled && !enabled)
			_meanBodyDepth = _demoBodyDepth;
		_demoBodyEnabled = enabled;
		_demoBodyOffsetX = offsetX;
		_demoBodyScale = Mathf.Max(0.01f, scale);
		_demoBodyDepth = Mathf.Clamp(depth, 0.0f, 1.0f);
		UpdateMaskMapping();
		if (enabled)
			foreach (var body in _bodyDetector.TrackedBodies)
				body.AvgDepth = _demoBodyDepth;
		if (ShowBodyDebug) _debugOverlay.QueueRedraw();
	}

	private void EmitOutwardParticle()
	{
		var sample = _maskPoints[_random.RandiRange(0, _maskPoints.Count - 1)];
		var closeness = GetCloseness(_demoBodyEnabled ? _demoBodyDepth : sample.NormalizedDepth);
		if (_random.Randf() > Mathf.Lerp(0.28f, 1.0f, closeness))
			return;

		var screenPosition = BodyMaskToScreen(sample.Position);
		// Share the density's depth thresholds: 40% fall speed far away, full speed up close.
		var depthSpeed = Mathf.Max(0.0f, OutwardSpeed) * Mathf.Lerp(0.4f, 1.0f, closeness);
		// The source is the full body. A small variation plus turbulence makes the
		// persistent particles read as a flowing multicolour trail, not an outline jet.
		var outwardVelocity = new Vector2(_random.RandfRange(-0.18f, 0.18f), 1.0f)
			* depthSpeed * _random.RandfRange(0.7f, 1.05f);

		_outwardParticleSystem.EmitParticle(
			new Transform2D(0.0f, screenPosition),
			outwardVelocity,
			Colors.White, Colors.White,
			(uint)(GpuParticles2D.EmitFlags.Position | GpuParticles2D.EmitFlags.Velocity));
	}

	private float GetCloseness(float normalizedDepth)
	{
		var depthSpan = Mathf.Max(0.01f, DepthNearThreshold - DepthFarThreshold);
		return Mathf.Clamp((normalizedDepth - DepthFarThreshold) / depthSpan, 0.0f, 1.0f);
	}

	public Vector2 BodyMaskToScreen(Vector2 maskPoint)
	{
		return _bodyToScreen * maskPoint;
	}

	private GpuParticles2D CreateOutwardParticleSystem()
	{
		// Stay visible during travel, then progressively dissolve before the lifetime cutoff.
		var alphaCurve = new Curve();
		alphaCurve.AddPoint(new Vector2(0.0f, 0.0f));
		// Soft birth, then a long low-opacity plateau. This exposes the complete hue
		// cycle along the trail instead of letting one bright age slice dominate it.
		alphaCurve.AddPoint(new Vector2(0.08f, 0.015f));
		alphaCurve.AddPoint(new Vector2(0.20f, 0.045f));
		alphaCurve.AddPoint(new Vector2(0.68f, 0.030f));
		alphaCurve.AddPoint(new Vector2(0.90f, 0.004f));
		alphaCurve.AddPoint(new Vector2(1.0f, 0.0f));

		var scaleCurve = new Curve();
		scaleCurve.MaxValue = 1.4f;
		scaleCurve.AddPoint(new Vector2(0.0f, 0.8f));
		scaleCurve.AddPoint(new Vector2(0.4f, 1.05f));
		scaleCurve.AddPoint(new Vector2(1.0f, 1.35f));

		// Complete one hue cycle while the particle is actually visible.  If the cycle
		// is spread over its full lifetime, the transparent birth/death sections hide
		// green, cyan or violet and leave only a small red/magenta slice onscreen.
		var hueRamp = new Gradient();
		const int hueSteps = 12;
		const float visibleStart = 0.12f;
		const float visibleEnd = 0.84f;
		for (var i = 0; i <= hueSteps; i++)
		{
			var progress = i / (float)hueSteps;
			var visibleProgress = (progress - visibleStart) / (visibleEnd - visibleStart);
			var hue = Mathf.PosMod(0.5f - visibleProgress, 1.0f);
			var color = Color.FromHsv(hue, 0.92f, 1.0f);
			if (i == 0)
				hueRamp.SetColor(0, color);
			else
				hueRamp.AddPoint(progress, color);
		}

		// Let particles leave their source before the shared flow bends their trajectories.
		var turbulenceCurve = new Curve();
		turbulenceCurve.AddPoint(new Vector2(0.0f, 0.0f));
		turbulenceCurve.AddPoint(new Vector2(0.25f, 0.5f));
		turbulenceCurve.AddPoint(new Vector2(0.6f, 1.0f));
		turbulenceCurve.AddPoint(new Vector2(1.0f, 0.5f));
		var material = new ParticleProcessMaterial
		{
			ParticleFlagDisableZ = true,
			TurbulenceNoiseStrength = 1.0f,
			TurbulenceNoiseSpeed = Vector3.Zero,
			TurbulenceInfluenceOverLife = new CurveTexture { Curve = turbulenceCurve },
			TurbulenceInitialDisplacementMin = 0.0f,
			TurbulenceInitialDisplacementMax = 0.0f,
			AngleMin = 0.0f,
			AngleMax = 360.0f,
			AngularVelocityMin = -12.0f,
			AngularVelocityMax = 12.0f,
			LifetimeRandomness = 0.12f,
			InitialVelocityMin = 0.0f,
			InitialVelocityMax = 0.0f,
			ScaleCurve = new CurveTexture { Curve = scaleCurve },
			AlphaCurve = new CurveTexture { Curve = alphaCurve },
			ColorRamp = new GradientTexture1D { Gradient = hueRamp },
			Color = Colors.White,
		};

		return new GpuParticles2D
		{
			Name = "OutwardSmoke",
			Amount = Mathf.Max(1, MaxOutwardParticles),
			Lifetime = Mathf.Max(0.1f, OutwardLifetime),
			LocalCoords = false,
			Emitting = false,
			FixedFps = 60,
			Interpolate = true,
			TextureFilter = CanvasItem.TextureFilterEnum.Linear,
			Texture = CreateCloudTexture(CloudTextureSize),
			ProcessMaterial = material,
			VisibilityRect = new Rect2(-300, -300, 10600, 10600),
		};
	}

	private void ApplyOutwardSettings()
	{
		var settings = new OutwardSettings(OutwardLifetime, MaxOutwardParticles, OutwardSize, OutwardGravity,
			OutwardDamping, OutwardTurbulenceEnabled, OutwardTurbulenceInfluence,
			OutwardTurbulenceScale, OutwardTurbulenceEvolution);
		if (_appliedOutwardSettings == settings)
			return;
		var material = (ParticleProcessMaterial)_outwardParticleSystem.ProcessMaterial;
		material.Gravity = new Vector3(0, OutwardGravity, 0);
		material.DampingMin = Mathf.Max(0, OutwardDamping);
		material.DampingMax = material.DampingMin * 1.5f;
		material.ScaleMin = Mathf.Max(1, OutwardSize) / CloudTextureSize * 0.85f;
		material.ScaleMax = Mathf.Max(1, OutwardSize) / CloudTextureSize * 1.15f;
		material.TurbulenceEnabled = OutwardTurbulenceEnabled;
		material.TurbulenceInfluenceMin = Mathf.Clamp(OutwardTurbulenceInfluence, 0, 1) * 0.5f;
		material.TurbulenceInfluenceMax = Mathf.Clamp(OutwardTurbulenceInfluence, 0, 1);
		material.TurbulenceNoiseScale = Mathf.Clamp(OutwardTurbulenceScale, 0, 9.99f);
		material.TurbulenceNoiseSpeedRandom = Mathf.Max(0, OutwardTurbulenceEvolution);
		if (_outwardParticleSystem.Amount != Mathf.Max(1, MaxOutwardParticles))
			_outwardParticleSystem.Amount = Mathf.Max(1, MaxOutwardParticles);
		if (_outwardParticleSystem.Lifetime != Mathf.Max(0.1f, OutwardLifetime))
			_outwardParticleSystem.Lifetime = Mathf.Max(0.1f, OutwardLifetime);
		_appliedOutwardSettings = settings;
	}

	private const int CloudTextureSize = 128;

	// Connected, low-frequency lobes: noise distorts the shape, never punches holes in it.
	private static ImageTexture CreateCloudTexture(int size)
	{
		using var noise = new FastNoiseLite { Seed = 173, Frequency = 0.018f };
		using var image = Image.CreateEmpty(size, size, false, Image.Format.Rgba8);
		var center = (size - 1) * 0.5f;
		for (var y = 0; y < size; y++)
		{
			for (var x = 0; x < size; x++)
			{
				var p = new Vector2(x - center, y - center) / center;
				var envelope = 1.0f - Mathf.SmoothStep(0.65f, 1.0f, p.Length());
				var warp = new Vector2(noise.GetNoise2D(x, y), noise.GetNoise2D(x + size, y)) * 0.16f;
				p += warp;
				var a = (p + new Vector2(0.20f, -0.12f)) / new Vector2(0.92f, 0.66f);
				var b = (p - new Vector2(0.23f, -0.12f)) / new Vector2(0.62f, 0.98f);
				var c = (p - new Vector2(0.12f, 0.33f)) / new Vector2(0.68f, 0.55f);
				var density = 0.65f * Mathf.Exp(-4.0f * a.LengthSquared())
					+ 0.55f * Mathf.Exp(-4.0f * b.LengthSquared())
					+ 0.40f * Mathf.Exp(-4.0f * c.LengthSquared());
				image.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp(density * envelope * 0.65f, 0, 1)));
			}
		}
		return ImageTexture.CreateFromImage(image);
	}

	private readonly record struct OutwardSettings(float Lifetime, int Maximum, float Size, float Gravity,
		float Damping, bool Turbulence, float Influence, float Scale, float Evolution);
}

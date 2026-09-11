#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Gestionnaire principal de rendu de particules (ChromAura).
/// Gère les systèmes de particules GPU pour la silhouette corporelle et les tracés de dessin,
/// applique les palettes de couleurs par corps détecté, délègue la segmentation à BodyDetector,
/// et convertit les tracés terminés en corps rigides physiques 2D (avec bascule de gravité Touche 2).
/// </summary>
public partial class particles : CanvasLayer
{
	/// <summary>Émis lorsque le nombre de corps détectés change.</summary>
	[Signal] public delegate void BodyCountChangedEventHandler(int count);

	/// <summary>Émis lorsqu'un tir de pistolet est effectué.</summary>
	[Signal] public delegate void GunShotFiredEventHandler(int trackId, Vector2 screenPos, Vector2 direction);

	/// <summary>Émis lorsque l'easter egg du cri de Wilhelm est déclenché.</summary>
	[Signal] public delegate void WilhelmEasterEggTriggeredEventHandler(Vector2 screenPos);

	/// <summary>Émis lorsque le mode de dessin bascule entre normal et physique.</summary>
	[Signal] public delegate void DrawingModeChangedEventHandler(bool isPhysics);
	[ExportGroup("Traînée multicolore")]
	[Export(PropertyHint.Range, "0.1,3,0.05")] public float OutwardLifetime { get; set; } = 0.4f;
	[Export] public int OutwardParticlesPerSecond { get; set; } = 8000;
	[Export] public int MaxOutwardParticles { get; set; } = 18000;
	// Initial fall speed. It is depth-linked and shared by every body sample.
	[Export] public float OutwardSpeed { get; set; } = 8.0f;
	[Export] public float OutwardSize { get; set; } = 100.0f;
	[Export] public float OutwardGravity { get; set; } = 75.0f;
	[Export(PropertyHint.Range, "0,10,0.1")] public float OutwardDamping { get; set; } = 10.0f;
	// Optical density, not RGB amplification: zero hides smoke without whitening its colors.
	[Export(PropertyHint.Range, "0,4,0.05,or_greater")] public float OutwardIntensity { get; set; } = 1.0f;
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

	[ExportSubgroup("Réaction du fond")]
	[Export] public float GunBackgroundPushRadius { get; set; } = 120.0f;
	[Export] public float GunBackgroundPushStrength { get; set; } = 210.0f;
	[Export] public float GunBackgroundPushLifetime { get; set; } = 0.45f;
	[Export] public float GunBackgroundPushTravelSpeed { get; set; } = 1200.0f;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float GunBackgroundPushForwardBias { get; set; } = 0.20f;
	[Export] public float GunBackgroundPushMaxSpeed { get; set; } = 260.0f;

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

	[ExportGroup("Physique et Dessin")]
	/// <summary>Active ou désactive la gravité sur les objets dessinés (Bascule avec la touche 2).</summary>
	[Export] public bool GravityEnabled { get; set; } = false;

	/// <summary>Épaisseur (rayon) des collisionneurs physiques pour les tracés dessinés.</summary>
	[Export] public float DrawingColliderRadius { get; set; } = 10.0f;

	// =========================================================================
	// PROPRIÉTÉS PUBLIQUES
	// =========================================================================

	/// <summary>Nombre de corps actuellement détectés et suivis.</summary>
	public int DetectedBodyCount => _bodyDetector.DetectedBodyCount;

	/// <summary>Accès en lecture seule à la liste des corps suivis.</summary>
	public IReadOnlyList<TrackedBody> TrackedBodies => _bodyDetector.TrackedBodies;

	/// <summary>Nombre de zones libérées en attente d'émission (test/debug).</summary>
	public int PendingReleasedTrailCount => _releasedTrailSamples.Count;

	// =========================================================================
	// ÉTAT INTERNE
	// =========================================================================

	private GpuParticles2D _outwardParticleSystem = null!;
	private float _outwardEmissionRemainder;
	private float _meanBodyDepth;
	private float _meanReleasedDepth;
	private readonly Queue<ReleasedMaskSample> _releasedTrailSamples = new();
	private readonly List<ReleasedMaskSample> _releasedTrailBatch = new();
	private bool[] _previousMaskActive = Array.Empty<bool>();
	private bool[] _confirmedMaskActive = Array.Empty<bool>();
	private float[] _previousMaskDepth = Array.Empty<float>();
	private int _releaseGridWidth;
	private int _releaseGridHeight;
	private int _releaseGridStride;
	private bool[] _previousDemoActive = Array.Empty<bool>();
	private bool[] _confirmedDemoActive = Array.Empty<bool>();
	private float[] _previousDemoDepth = Array.Empty<float>();
	private bool[] _currentDemoActive = Array.Empty<bool>();
	private float[] _currentDemoDepth = Array.Empty<float>();
	private Vector2I _demoReleaseGridSize;
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

	// Pipelines visuels particules GPU (corps + tracé en direct),
	// pipeline double pour le tracé de dessin persistant,
	// et délégation complète du geste pistolet à GunParticleManager.
	private readonly GpuParticles2D[] _mistParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _sparkleParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _trailCoreParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];
	private readonly GpuParticles2D[] _trailSparkleParticleSystems = new GpuParticles2D[BodyPalette.DefaultPalettes.Length];

	private readonly GunParticleManager _gunParticleManager = new();
	private readonly List<Vector2> _smoothedFingerPos = new();

	private AmbientFireflies _ambientFireflies = null!;

	private sealed class ActiveStroke
	{
		public List<Vector2> Points { get; } = new();
		public int PaletteIndex { get; set; }
	}

	// Système de capture de tracé et instanciation physique
	private readonly Dictionary<int, ActiveStroke> _activeFingerStrokes = new();
	private readonly List<DrawnBody2D> _spawnedDrawnBodies = new();
	private Node2D _physicsContainer = null!;
	private StaticBody2D? _boundaryBody;
	private CollisionShape2D? _floorShape;
	private CollisionShape2D? _leftWallShape;
	private CollisionShape2D? _rightWallShape;

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
			GunPushRadius = GunBackgroundPushRadius,
			GunPushStrength = GunBackgroundPushStrength,
			GunPushLifetime = GunBackgroundPushLifetime,
			GunPushTravelSpeed = GunBackgroundPushTravelSpeed,
			GunPushForwardBias = GunBackgroundPushForwardBias,
			GunPushMaxSpeed = GunBackgroundPushMaxSpeed,
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

		// Initialisation du conteneur pour tous les corps physiques dessinés
		_physicsContainer = new Node2D { Name = "PhysicalDrawings" };
		AddChild(_physicsContainer);

		SetupBoundaries();

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
		// A released region keeps its last depth while it drains from the queue.
		var intensityDepth = _releasedTrailSamples.Count > 0 ? _meanReleasedDepth
			: (_demoBodyEnabled ? _demoBodyDepth : _meanBodyDepth);
		var closeness = GetCloseness(intensityDepth);
		// Keep the far-depth look as the baseline. Depth only has a deliberately tiny
		// influence so moving close cannot turn overlapping rainbow smoke white.
		var depthIntensityScale = Mathf.Lerp(0.4f, 0.45f, Mathf.SmoothStep(0.0f, 1.0f, closeness));
		var outwardIntensity = Mathf.Max(0.0f, OutwardIntensity) * depthIntensityScale;
		// Native alpha blending: intensity controls opacity only, not RGB brightness.
		_outwardParticleSystem.SelfModulate = new Color(1.0f, 1.0f, 1.0f,
			1.0f - Mathf.Exp(-outwardIntensity));
		_elapsedTime += delta;
		_lastFingerUpdateTime += delta;
		var isPointingActive = _isPointingUp && (_lastFingerUpdateTime < 0.35);

		// 1. Émission des particules pendant le dessin et capture silencieuse du tracé
		if (isPointingActive && _currentFingers.Count > 0)
		{
			EmitFingerTrails();
			RecordLiveDrawingStrokes();
		}
		else
		{
			FinalizeAllActiveStrokes();
			_prevFingerScreenPos.Clear();
			_smoothedFingerPos.Clear();
		}

		// 1.5. Émission des tirs de particules pour le geste pistolet (Finger Gun)
		_gunParticleManager.ProcessGunFirings(
			_elapsedTime,
			NormalizedToScreen,
			GetPaletteIndexForTrack,
			(trackId, pos, dir) => HandleGunShotFired(trackId, pos, dir)
		);

		// 2. Émission des particules de silhouette corporelle
		// Do not catch up an entire stalled frame with a burst of particles.
		var emissionDelta = (float)Math.Clamp(delta, 0.0, 0.1);
		if (_maskPoints.Count > 0)
		{
			_emissionRemainder += Mathf.Max(0, ParticlesPerSecond) * emissionDelta;
			var particleCount = Mathf.FloorToInt(_emissionRemainder);
			_emissionRemainder -= particleCount;

			for (var i = 0; i < particleCount; i++)
				EmitSampledParticle(isPointingActive);
		}
		else
		{
			_emissionRemainder = 0.0f;
		}

		// OutwardSmoke drains only the zones which the body has just released.
		var requestedRate = Mathf.Max(0, OutwardParticlesPerSecond);
		var safeRate = _outwardParticleSystem.Amount * 0.9f / (float)_outwardParticleSystem.Lifetime;
		if (_releasedTrailSamples.Count == 0)
		{
			_outwardEmissionRemainder = 0.0f;
			return;
		}
		_outwardEmissionRemainder += Mathf.Min(requestedRate, safeRate) * emissionDelta;
		var outwardParticleCount = Mathf.FloorToInt(_outwardEmissionRemainder);
		_outwardEmissionRemainder -= outwardParticleCount;
		for (var i = 0; i < outwardParticleCount && _releasedTrailSamples.Count > 0; i++)
			EmitOutwardParticle();
	}

	/// <summary>
	/// Gestion des raccourcis clavier utilisateur pour le contrôle en temps réel.
	/// </summary>
	public override void _Input(InputEvent @event)
	{
		if (@event is InputEventKey keyEvent && keyEvent.Pressed && !keyEvent.Echo)
		{
			var physicalKey = keyEvent.PhysicalKeycode;
			var keycode = keyEvent.Keycode;
			var unicode = (char)keyEvent.Unicode;

			// Touche 2 (AZERTY é, QWERTY 2, Pavé num 2) : Bascule de la gravité
			if (physicalKey == Key.Key2 || keycode == Key.Key2 || keycode == Key.Kp2 ||
				unicode == '2' || unicode == 'é')
			{
				ToggleGravity();
			}
			// Touche C : Nettoyer tous les dessins
			else if (physicalKey == Key.C || keycode == Key.C || unicode == 'c' || unicode == 'C')
			{
				ClearAllDrawings();
			}
			// Touche D : Bascule de l'affichage des boîtes de débug
			else if (physicalKey == Key.D || keycode == Key.D || unicode == 'd' || unicode == 'D')
			{
				ShowBodyDebug = !ShowBodyDebug;
				if (_debugOverlay != null)
				{
					_debugOverlay.IsVisibleInGame = ShowBodyDebug;
					_debugOverlay.QueueRedraw();
				}
			}
			// Touche P : Cycle manuel des palettes pour prévisualisation
			else if (physicalKey == Key.P || keycode == Key.P || unicode == 'p' || unicode == 'P')
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
			(tid, screenPos, dir) => HandleGunShotFired(tid, screenPos, dir)
		);
	}

	private void HandleGunShotFired(int trackId, Vector2 screenPos, Vector2 direction)
	{
		_ambientFireflies?.AddGunShotPush(screenPos, direction);
		EmitSignal(SignalName.GunShotFired, trackId, screenPos, direction);
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

	/// <summary>
	/// Change aléatoirement la palette de couleurs du corps détecté le plus proche de la main (Geste Face Palm).
	/// </summary>
	public void ChangeBodyColorRandomly(Vector2 normalizedHandPos)
	{
		_ChangeBodyColorRandomlyInternal(normalizedHandPos);
	}

	/// <summary>
	/// Surcharge sans argument pour compatibilité GDScript.
	/// </summary>
	public void ChangeBodyColorRandomly()
	{
		_ChangeBodyColorRandomlyInternal(null);
	}

	private void _ChangeBodyColorRandomlyInternal(Vector2? normalizedHandPos)
	{
		var palettes = BodyPalette.DefaultPalettes;
		if (palettes.Length == 0)
			return;

		if (_bodyDetector.TrackedBodies.Count == 0)
		{
			var currentOffset = PaletteOffset % palettes.Length;
			var newOffset = currentOffset;
			if (palettes.Length > 1)
			{
				while (newOffset == currentOffset)
				{
					newOffset = _random.RandiRange(0, palettes.Length - 1);
				}
			}
			PaletteOffset = newOffset;

			for (var i = 0; i < _maskPoints.Count; i++)
			{
				var pt = _maskPoints[i];
				_maskPoints[i] = new MaskSample(pt.Position, pt.NormalizedDepth, PaletteOffset, pt.BodyId);
			}
			EmitSignal(SignalName.BodyCountChanged, 0);
			return;
		}

		TrackedBody targetBody = _bodyDetector.TrackedBodies[0];
		if (normalizedHandPos.HasValue)
		{
			var handMask = new Vector2(
				normalizedHandPos.Value.X * _maskSize.X,
				normalizedHandPos.Value.Y * _maskSize.Y
			);
			var bestDist = float.MaxValue;
			foreach (var body in _bodyDetector.TrackedBodies)
			{
				var dist = handMask.DistanceTo(body.Centroid);
				if (dist < bestDist)
				{
					bestDist = dist;
					targetBody = body;
				}
			}
		}

		var curPal = targetBody.PaletteIndex % palettes.Length;
		var nextPal = curPal;
		if (palettes.Length > 1)
		{
			while (nextPal == curPal)
			{
				nextPal = _random.RandiRange(0, palettes.Length - 1);
			}
		}
		targetBody.PaletteIndex = nextPal;

		for (var i = 0; i < _maskPoints.Count; i++)
		{
			if (_maskPoints[i].BodyId == targetBody.Id)
			{
				var pt = _maskPoints[i];
				_maskPoints[i] = new MaskSample(pt.Position, pt.NormalizedDepth, nextPal, pt.BodyId);
			}
		}

		EmitSignal(SignalName.BodyCountChanged, _bodyDetector.DetectedBodyCount);
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
		UpdateReleasedMaskZones(depthImage);
		if (_demoBodyEnabled)
			UpdateReleasedDemoZones();

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
				var steps = Mathf.Max(2, Mathf.CeilToInt(distance / 1.4f));
				for (var s = 0; s <= steps; s++)
				{
					var t = (float)s / steps;
					var center = prevScreen.Lerp(currentScreen, t);
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
	// GESTION DU DESSIN PHYSIQUE & LIMITES D'ÉCRAN
	// =========================================================================

	/// <summary>
	/// Enregistre les points du geste en cours silencieusement (sans affichage vectoriel parasite).
	/// </summary>
	private void RecordLiveDrawingStrokes()
	{
		for (var i = 0; i < _currentFingers.Count; i++)
		{
			var currentScreen = (i < _smoothedFingerPos.Count)
				? _smoothedFingerPos[i]
				: NormalizedToScreen(_currentFingers[i]);

			if (!_activeFingerStrokes.TryGetValue(i, out var stroke))
			{
				stroke = new ActiveStroke();
				_activeFingerStrokes[i] = stroke;
			}

			// Conserver la palette exacte associée au doigt qui dessine
			var paletteIndex = (i < _fingerPaletteIndices.Count)
				? (_fingerPaletteIndices[i] % BodyPalette.DefaultPalettes.Length)
				: (PaletteOffset % BodyPalette.DefaultPalettes.Length);
			stroke.PaletteIndex = paletteIndex;

			if (stroke.Points.Count == 0 || stroke.Points[^1].DistanceTo(currentScreen) >= 4.0f)
			{
				stroke.Points.Add(currentScreen);
			}
		}

		// Finalise les traits pour les doigts qui ont arrêté de pointer
		var activeIndices = new HashSet<int>();
		for (var i = 0; i < _currentFingers.Count; i++)
		{
			activeIndices.Add(i);
		}

		var fingersToFinalize = new List<int>();
		foreach (var fingerIdx in _activeFingerStrokes.Keys)
		{
			if (!activeIndices.Contains(fingerIdx))
			{
				fingersToFinalize.Add(fingerIdx);
			}
		}

		foreach (var fingerIdx in fingersToFinalize)
		{
			FinalizeStroke(fingerIdx);
		}
	}

	/// <summary>
	/// Finalise tous les tracés actifs quand le geste s'arrête.
	/// </summary>
	private void FinalizeAllActiveStrokes()
	{
		if (_activeFingerStrokes.Count == 0)
			return;

		var fingers = new List<int>(_activeFingerStrokes.Keys);
		foreach (var fingerIdx in fingers)
		{
			FinalizeStroke(fingerIdx);
		}
		_activeFingerStrokes.Clear();
	}

	/// <summary>
	/// Convertit le tracé d'un doigt en corps rigide physique DrawnBody2D (uniquement si Touche 2 est active).
	/// </summary>
	private void FinalizeStroke(int fingerIndex)
	{
		if (!_activeFingerStrokes.TryGetValue(fingerIndex, out var stroke))
			return;

		// Uniquement créer un objet physique si le mode physique (Touche 2) est enclenché
		if (GravityEnabled && stroke.Points.Count >= 2 && GetPolylineLength(stroke.Points) > 8.0f)
		{
			var palettes = BodyPalette.DefaultPalettes;
			var paletteIndex = stroke.PaletteIndex % palettes.Length;
			var palette = palettes[paletteIndex];

			var drawnBody = new DrawnBody2D();
			_physicsContainer.AddChild(drawnBody);
			drawnBody.Initialize(
				stroke.Points,
				palette,
				true,
				DrawingColliderRadius
			);
			_spawnedDrawnBodies.Add(drawnBody);
			CheckMaxDrawnBodies();

			// Supprime immédiatement les particules résiduelles pour que seul l'objet physique subsiste
			_trailCoreParticleSystems[paletteIndex].Restart();
			_trailCoreParticleSystems[paletteIndex].Emitting = false;
			_trailSparkleParticleSystems[paletteIndex].Restart();
			_trailSparkleParticleSystems[paletteIndex].Emitting = false;
		}

		_activeFingerStrokes.Remove(fingerIndex);
	}

	private static float GetPolylineLength(IReadOnlyList<Vector2> points)
	{
		var length = 0.0f;
		for (var i = 0; i < points.Count - 1; i++)
		{
			length += points[i].DistanceTo(points[i + 1]);
		}
		return length;
	}

	private void CheckMaxDrawnBodies()
	{
		const int maxBodies = 120;
		while (_spawnedDrawnBodies.Count > maxBodies)
		{
			var oldest = _spawnedDrawnBodies[0];
			_spawnedDrawnBodies.RemoveAt(0);
			if (IsInstanceValid(oldest))
			{
				oldest.QueueFree();
			}
		}
	}

	/// <summary>
	/// Bascule le mode de dessin entre normal et physique (Geste Rock and Roll ou Touche 2).
	/// </summary>
	public bool ToggleDrawingMode()
	{
		ToggleGravity();
		ClearAllDrawings();
		return GravityEnabled;
	}

	/// <summary>
	/// Bascule l'état de gravité pour tous les objets dessinés (Touche 2).
	/// Lors du passage en mode physique, efface les objets physiques précédemment dessinés.
	/// </summary>
	public void ToggleGravity()
	{
		GravityEnabled = !GravityEnabled;
		if (GravityEnabled)
		{
			ClearAllDrawings();
		}
		else
		{
			foreach (var body in _spawnedDrawnBodies)
			{
				if (IsInstanceValid(body))
				{
					body.SetGravityActive(false);
				}
			}
		}
		EmitSignal(SignalName.DrawingModeChanged, GravityEnabled);
	}

	/// <summary>
	/// Efface tous les objets dessinés (Touche C).
	/// </summary>
	public void ClearAllDrawings()
	{
		foreach (var body in _spawnedDrawnBodies)
		{
			if (IsInstanceValid(body))
			{
				body.QueueFree();
			}
		}
		_spawnedDrawnBodies.Clear();
		_activeFingerStrokes.Clear();

		for (var i = 0; i < BodyPalette.DefaultPalettes.Length; i++)
		{
			_trailCoreParticleSystems[i].Restart();
			_trailCoreParticleSystems[i].Emitting = false;
			_trailSparkleParticleSystems[i].Restart();
			_trailSparkleParticleSystems[i].Emitting = false;
		}
	}

	private void SetupBoundaries()
	{
		_boundaryBody = new StaticBody2D { Name = "ScreenBoundaries" };

		_floorShape = new CollisionShape2D();
		_leftWallShape = new CollisionShape2D();
		_rightWallShape = new CollisionShape2D();

		_boundaryBody.AddChild(_floorShape);
		_boundaryBody.AddChild(_leftWallShape);
		_boundaryBody.AddChild(_rightWallShape);

		AddChild(_boundaryBody);

		UpdateBoundaryPositions();
		GetViewport().SizeChanged += UpdateBoundaryPositions;
	}

	private void UpdateBoundaryPositions()
	{
		var viewportSize = GetViewport().GetVisibleRect().Size;
		if (viewportSize.X <= 0 || viewportSize.Y <= 0)
			return;

		const float wallThickness = 120.0f;

		if (_floorShape != null)
		{
			_floorShape.Shape = new RectangleShape2D
			{
				Size = new Vector2(viewportSize.X * 2.0f, wallThickness)
			};
			_floorShape.Position = new Vector2(viewportSize.X * 0.5f, viewportSize.Y + (wallThickness * 0.5f));
		}

		if (_leftWallShape != null)
		{
			_leftWallShape.Shape = new RectangleShape2D
			{
				Size = new Vector2(wallThickness, viewportSize.Y * 2.0f)
			};
			_leftWallShape.Position = new Vector2(-(wallThickness * 0.5f), viewportSize.Y * 0.5f);
		}

		if (_rightWallShape != null)
		{
			_rightWallShape.Shape = new RectangleShape2D
			{
				Size = new Vector2(wallThickness, viewportSize.Y * 2.0f)
			};
			_rightWallShape.Position = new Vector2(viewportSize.X + (wallThickness * 0.5f), viewportSize.Y * 0.5f);
		}
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

	private void UpdateReleasedMaskZones(Image depthImage)
	{
		var stride = Mathf.Max(1, ClusterStride);
		var width = depthImage.GetWidth();
		var height = depthImage.GetHeight();
		var gridWidth = (width + stride - 1) / stride;
		var gridHeight = (height + stride - 1) / stride;
		EnsureMaskReleaseGrid(gridWidth, gridHeight, stride);

		Image? converted = null;
		if (depthImage.GetFormat() != Image.Format.Rgb8)
		{
			converted = (Image)depthImage.Duplicate();
			converted.Convert(Image.Format.Rgb8);
		}
		using (converted)
		{
			var pixels = (converted ?? depthImage).GetData();
			var hasActiveCell = false;
			_releasedTrailBatch.Clear();
			for (var gridY = 0; gridY < gridHeight; gridY++)
			{
				var y = Mathf.Min(gridY * stride, height - 1);
				for (var gridX = 0; gridX < gridWidth; gridX++)
				{
					var x = Mathf.Min(gridX * stride, width - 1);
					var index = gridY * gridWidth + gridX;
					var depth = pixels[(y * width + x) * 3] / 255.0f;
					var active = depth > 0.0f;
					if (_confirmedMaskActive[index] && !active)
					{
						_releasedTrailBatch.Add(new ReleasedMaskSample(
							new Vector2(x + stride * 0.5f, y + stride * 0.5f), _previousMaskDepth[index], false));
					}

					_confirmedMaskActive[index] = active && _previousMaskActive[index];
					_previousMaskActive[index] = active;
					_previousMaskDepth[index] = active ? depth : 0.0f;
					hasActiveCell |= active;
				}
			}
			QueueReleasedTrailBatch();
			if (!hasActiveCell)
				ResetMaskReleaseHistory();
		}
	}

	private void EnsureMaskReleaseGrid(int width, int height, int stride)
	{
		if (_releaseGridWidth == width && _releaseGridHeight == height && _releaseGridStride == stride)
			return;

		_releaseGridWidth = width;
		_releaseGridHeight = height;
		_releaseGridStride = stride;
		var count = width * height;
		_previousMaskActive = new bool[count];
		_confirmedMaskActive = new bool[count];
		_previousMaskDepth = new float[count];
	}

	private void ResetMaskReleaseHistory()
	{
		Array.Clear(_previousMaskActive, 0, _previousMaskActive.Length);
		Array.Clear(_confirmedMaskActive, 0, _confirmedMaskActive.Length);
		Array.Clear(_previousMaskDepth, 0, _previousMaskDepth.Length);
	}

	private void UpdateReleasedDemoZones()
	{
		if (_maskPoints.Count == 0)
		{
			ResetDemoReleaseHistory();
			return;
		}

		var viewport = GetViewport().GetVisibleRect().Size;
		if (viewport.X <= 1.0f || viewport.Y <= 1.0f)
			viewport = new Vector2(1920.0f, 1080.0f);
		var cellSize = Mathf.Max(4, ClusterStride * 2);
		var gridSize = new Vector2I(
			Mathf.CeilToInt(viewport.X / cellSize),
			Mathf.CeilToInt(viewport.Y / cellSize));
		EnsureDemoReleaseGrid(gridSize);
		Array.Clear(_currentDemoActive, 0, _currentDemoActive.Length);
		Array.Clear(_currentDemoDepth, 0, _currentDemoDepth.Length);

		foreach (var sample in _maskPoints)
		{
			var screenPosition = BodyMaskToScreen(sample.Position);
			var gridX = Mathf.FloorToInt(screenPosition.X / cellSize);
			var gridY = Mathf.FloorToInt(screenPosition.Y / cellSize);
			if (gridX < 0 || gridY < 0 || gridX >= gridSize.X || gridY >= gridSize.Y)
				continue;
			var index = gridY * gridSize.X + gridX;
			_currentDemoActive[index] = true;
			_currentDemoDepth[index] = Mathf.Max(_currentDemoDepth[index], sample.NormalizedDepth);
		}

		_releasedTrailBatch.Clear();
		for (var index = 0; index < _currentDemoActive.Length; index++)
		{
			if (_confirmedDemoActive[index] && !_currentDemoActive[index])
			{
				var gridX = index % gridSize.X;
				var gridY = index / gridSize.X;
				_releasedTrailBatch.Add(new ReleasedMaskSample(
					new Vector2((gridX + 0.5f) * cellSize, (gridY + 0.5f) * cellSize), _previousDemoDepth[index], true));
			}
			_confirmedDemoActive[index] = _currentDemoActive[index] && _previousDemoActive[index];
			_previousDemoActive[index] = _currentDemoActive[index];
			_previousDemoDepth[index] = _currentDemoActive[index] ? _currentDemoDepth[index] : 0.0f;
		}
		QueueReleasedTrailBatch();
	}

	private void EnsureDemoReleaseGrid(Vector2I size)
	{
		if (_demoReleaseGridSize == size)
			return;

		_demoReleaseGridSize = size;
		var count = size.X * size.Y;
		_previousDemoActive = new bool[count];
		_confirmedDemoActive = new bool[count];
		_previousDemoDepth = new float[count];
		_currentDemoActive = new bool[count];
		_currentDemoDepth = new float[count];
	}

	private void ResetDemoReleaseHistory()
	{
		Array.Clear(_previousDemoActive, 0, _previousDemoActive.Length);
		Array.Clear(_confirmedDemoActive, 0, _confirmedDemoActive.Length);
		Array.Clear(_previousDemoDepth, 0, _previousDemoDepth.Length);
		Array.Clear(_currentDemoActive, 0, _currentDemoActive.Length);
		Array.Clear(_currentDemoDepth, 0, _currentDemoDepth.Length);
	}

	private void QueueReleasedTrailBatch()
	{
		if (_releasedTrailBatch.Count == 0)
			return;

		// Shuffle one released frame before queueing it: a body edge is stored in
		// raster order, but must never appear as a scan in the particle trail.
		for (var i = _releasedTrailBatch.Count - 1; i > 0; i--)
		{
			var swapIndex = _random.RandiRange(0, i);
			(_releasedTrailBatch[i], _releasedTrailBatch[swapIndex]) =
				(_releasedTrailBatch[swapIndex], _releasedTrailBatch[i]);
		}

		var depthSum = 0.0f;
		var capacity = Mathf.Max(1, MaxOutwardParticles);
		foreach (var sample in _releasedTrailBatch)
		{
			while (_releasedTrailSamples.Count >= capacity)
				_releasedTrailSamples.Dequeue();
			_releasedTrailSamples.Enqueue(sample);
			depthSum += sample.NormalizedDepth;
		}
		_meanReleasedDepth = depthSum / _releasedTrailBatch.Count;
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
		{
			foreach (var body in _bodyDetector.TrackedBodies)
				body.AvgDepth = _demoBodyDepth;
			UpdateReleasedDemoZones();
		}
		else
		{
			ResetDemoReleaseHistory();
		}
		if (ShowBodyDebug) _debugOverlay.QueueRedraw();
	}

	private void EmitOutwardParticle()
	{
		var sample = _releasedTrailSamples.Dequeue();
		var closeness = GetCloseness(sample.NormalizedDepth);
		if (_random.Randf() > Mathf.Lerp(0.28f, 1.0f, closeness))
			return;

		var screenPosition = sample.IsScreenPosition ? sample.Position : BodyMaskToScreen(sample.Position);
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

		// One complete rainbow during the visible part of the particle lifetime.
		// The shared ramp keeps contiguous colour bands in a trail rather than random hues.
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

	private readonly record struct ReleasedMaskSample(Vector2 Position, float NormalizedDepth, bool IsScreenPosition);
	private readonly record struct OutwardSettings(float Lifetime, int Maximum, float Size, float Gravity,
		float Damping, bool Turbulence, float Influence, float Scale, float Evolution);
}

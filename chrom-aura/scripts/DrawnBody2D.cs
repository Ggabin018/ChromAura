#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Représente un tracé ou une forme dessinée (ligne, boîte, cube) converti en corps physique 2D rigide.
/// Intègre des collisionneurs volumiques, un rendu néon éthéré multi-passes,
/// et supporte la bascule de gravité (chute/empilement ou suspension en apesanteur).
/// </summary>
public partial class DrawnBody2D : RigidBody2D
{
	private Vector2[] _localPoints = Array.Empty<Vector2>();
	private Color _glowColor = Colors.White;
	private Color _midColor = Colors.White;
	private Color _coreColor = Colors.White;
	private bool _isClosed;
	private float _colliderRadius = 10.0f;

	/// <summary>
	/// Initialise la forme physique à partir des points en coordonnées écran et de la palette de couleurs.
	/// </summary>
	public void Initialize(
		IReadOnlyList<Vector2> screenPoints,
		BodyPalette palette,
		bool gravityActive,
		float colliderRadius = 10.0f)
	{
		if (screenPoints == null || screenPoints.Count < 2)
			return;

		_colliderRadius = colliderRadius;

		// 1. Calcul du barycentre (centre de masse)
		var center = Vector2.Zero;
		for (var i = 0; i < screenPoints.Count; i++)
		{
			center += screenPoints[i];
		}
		center /= screenPoints.Count;

		Position = center;
		ZIndex = 10;

		// 2. Conversion en coordonnées locales relatives au barycentre
		_localPoints = new Vector2[screenPoints.Count];
		for (var i = 0; i < screenPoints.Count; i++)
		{
			_localPoints[i] = screenPoints[i] - center;
		}

		// 3. Détection de forme fermée (ex: cube / boîte)
		var firstPoint = screenPoints[0];
		var lastPoint = screenPoints[^1];
		_isClosed = screenPoints.Count >= 4 && firstPoint.DistanceTo(lastPoint) < 40.0f;

		// 4. Configuration des couleurs identiques aux particules du tracé
		var startCol = palette.TrailStart;
		var endCol = palette.TrailEnd;
		var dominantCol = startCol.Lerp(endCol, 0.20f);
		_glowColor = new Color(dominantCol.R * 1.15f, dominantCol.G * 1.15f, dominantCol.B * 1.15f, 0.45f);
		_midColor = new Color(dominantCol.R * 1.15f, dominantCol.G * 1.15f, dominantCol.B * 1.15f, 0.95f);
		_coreColor = Colors.White.Lerp(dominantCol, 0.30f);

		// 5. Configuration physique (frottement élevé pour bon empilement)
		PhysicsMaterialOverride = new PhysicsMaterial
		{
			Friction = 0.90f,
			Rough = true,
			Bounce = 0.05f,
		};

		LinearDamp = 0.6f;
		AngularDamp = 1.2f;
		ContinuousCd = CcdMode.CastRay;

		// 6. Création des collisionneurs volumiques le long des segments
		BuildColliders();

		// 7. Application de l'état de gravité initial
		SetGravityActive(gravityActive);

		// Matériau pour le rendu lumineux
		Material = new CanvasItemMaterial
		{
			BlendMode = CanvasItemMaterial.BlendModeEnum.Add,
			LightMode = CanvasItemMaterial.LightModeEnum.Unshaded,
		};

		QueueRedraw();
	}

	/// <summary>
	/// Active ou désactive la gravité et la simulation dynamique du corps.
	/// En apesanteur (active = false), le corps se fige dans l'espace.
	/// </summary>
	public void SetGravityActive(bool active)
	{
		FreezeMode = FreezeModeEnum.Static;
		if (active)
		{
			Freeze = false;
			GravityScale = 1.0f;
			Sleeping = false;
			ApplyCentralImpulse(new Vector2(0.0f, 15.0f));
		}
		else
		{
			Freeze = true;
			LinearVelocity = Vector2.Zero;
			AngularVelocity = 0.0f;
		}
	}

	/// <summary>
	/// Construit des formes de collision (capsules et cercles) le long de chaque segment.
	/// </summary>
	private void BuildColliders()
	{
		if (_localPoints.Length < 2)
			return;

		var segmentCount = _isClosed ? _localPoints.Length : _localPoints.Length - 1;

		for (var i = 0; i < segmentCount; i++)
		{
			var p1 = _localPoints[i];
			var p2 = _localPoints[(i + 1) % _localPoints.Length];

			var distance = p1.DistanceTo(p2);
			if (distance < 1.0f)
				continue;

			// Capsule orientée le long du segment
			var capsule = new CapsuleShape2D
			{
				Radius = _colliderRadius,
				Height = distance + (_colliderRadius * 2.0f),
			};

			var colShape = new CollisionShape2D
			{
				Shape = capsule,
				Position = (p1 + p2) * 0.5f,
				Rotation = (p2 - p1).Angle() + Mathf.Pi * 0.5f,
			};

			AddChild(colShape);
		}

		// Cercles de collision aux sommets pour adoucir les jonctions d'angles
		for (var i = 0; i < _localPoints.Length; i++)
		{
			var circle = new CircleShape2D
			{
				Radius = _colliderRadius,
			};
			var colShape = new CollisionShape2D
			{
				Shape = circle,
				Position = _localPoints[i],
			};
			AddChild(colShape);
		}
	}

	public override void _Draw()
	{
		if (_localPoints.Length < 2)
			return;

		// 1. Halo externe doux et diffus
		DrawPolyline(_localPoints, _glowColor, 18.0f, true);

		// 2. Trait médian coloré et saturé
		DrawPolyline(_localPoints, _midColor, 9.0f, true);

		// 3. Cœur interne brillant
		DrawPolyline(_localPoints, _coreColor, 3.0f, true);

		// Fermeture visuelle si boucle
		if (_isClosed)
		{
			var pLast = _localPoints[^1];
			var pFirst = _localPoints[0];
			DrawLine(pLast, pFirst, _glowColor, 18.0f, true);
			DrawLine(pLast, pFirst, _midColor, 9.0f, true);
			DrawLine(pLast, pFirst, _coreColor, 3.0f, true);
		}

		// 4. Extrémités arrondies pour traits ouverts
		if (!_isClosed && _localPoints.Length > 0)
		{
			DrawCircle(_localPoints[0], 4.5f, _midColor);
			DrawCircle(_localPoints[0], 1.5f, _coreColor);
			DrawCircle(_localPoints[^1], 4.5f, _midColor);
			DrawCircle(_localPoints[^1], 1.5f, _coreColor);
		}
	}
}

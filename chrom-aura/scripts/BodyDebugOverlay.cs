#nullable enable
using Godot;
using System.Collections.Generic;

/// <summary>
/// Mode développeur : Composant d'affichage visuel (overlay) affichant les boîtes englobantes,
/// centroïdes et libellés textuels des différents corps détectés par le capteur Kinect.
/// </summary>
public partial class BodyDebugOverlay : Control
{
	/// <summary>Active ou désactive l'affichage du mode développeur (boîtes et labels).</summary>
	[Export] public bool IsVisibleInGame { get; set; } = false;

	private particles? _particleController;

	/// <summary>
	/// Initialise l'overlay en le liant au contrôleur de particules principal.
	/// </summary>
	public void Initialize(particles particleController)
	{
		_particleController = particleController;
		MouseFilter = MouseFilterEnum.Ignore;
		SetAnchorsPreset(LayoutPreset.FullRect);
	}

	/// <summary>
	/// Rendu 2D des boîtes englobantes et des libellés de chaque personne active.
	/// </summary>
	public override void _Draw()
	{
		if (!IsVisibleInGame || _particleController == null)
			return;

		var bodies = _particleController.TrackedBodies;
		if (bodies.Count == 0)
			return;

		var font = ThemeDB.FallbackFont;
		const int fontSize = 16;
		var palettes = BodyPalette.DefaultPalettes;

		foreach (var body in bodies)
		{
			var pal = palettes[body.PaletteIndex % palettes.Length];
			var pMin = _particleController.BodyMaskToScreen(body.BoundingBox.Position);
			var pMax = _particleController.BodyMaskToScreen(body.BoundingBox.End);
			var rect = new Rect2(pMin, pMax - pMin);

			// 1. Contour de la boîte englobante (couleur proche de la palette)
			DrawRect(rect, new Color(pal.ColorNear.R, pal.ColorNear.G, pal.ColorNear.B, 0.85f), false, 2.0f);

			// 2. Marqueur du centre de masse (centroïde)
			var screenCentroid = _particleController.BodyMaskToScreen(body.Centroid);
			DrawCircle(screenCentroid, 5.0f, pal.ColorFar);

			// 3. Étiquette texte (Personne X, Nom de la palette, Pointeur actif)
			var labelText = $"Personne {body.Id} ({pal.Name}){(body.IsPointing ? " ✍️" : "")}";
			var textPos = new Vector2(rect.Position.X, Mathf.Max(rect.Position.Y - 6.0f, 22.0f));
			DrawString(font, textPos, labelText, HorizontalAlignment.Left, -1, fontSize, pal.ColorNear);
		}
	}
}

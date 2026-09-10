#nullable enable
using Godot;

/// <summary>
/// Définit une palette de couleurs bicolore (proche / loin) pour les particules d'un corps,
/// ainsi qu'une palette lumineuse dédiée aux tracés de doigts persistants.
/// </summary>
public readonly record struct BodyPalette(
	string Name,
	Color ColorNear,
	Color ColorFar,
	Color TrailStart,
	Color TrailEnd
)
{
	/// <summary>
	/// Interpole la couleur de la particule de corps selon la profondeur normalisée (t entre 0 et 1).
	/// t = 0 : particule éloignée (ColorFar).
	/// t = 1 : particule proche de la caméra (ColorNear).
	/// </summary>
	public Color EvaluateBody(float t)
	{
		return ColorFar.Lerp(ColorNear, t);
	}

	/// <summary>
	/// Interpole la couleur pour le tracé persistant du pointeur (dessin).
	/// </summary>
	public Color EvaluateTrail(float t)
	{
		return TrailStart.Lerp(TrailEnd, t);
	}

	/// <summary>
	/// Les 5 palettes de couleurs lumineuses par défaut, conçues pour un contraste maximal.
	/// </summary>
	public static readonly BodyPalette[] DefaultPalettes = new[]
	{
		// Palette 1 : Bleu-Vert (Cyan électrique à Émeraude néon)
		new BodyPalette(
			"Bleu-Vert",
			new Color(0.0f, 0.95f, 1.0f, 0.98f),
			new Color(0.0f, 1.0f, 0.48f, 0.95f),
			new Color(0.15f, 1.0f, 0.88f, 0.98f),
			new Color(0.0f, 0.85f, 0.40f, 0.98f)
		),
		// Palette 2 : Violet-Rose (Violet cosmique à Magenta éclatant)
		new BodyPalette(
			"Violet-Rose",
			new Color(0.65f, 0.15f, 1.0f, 0.98f),
			new Color(1.0f, 0.12f, 0.65f, 0.95f),
			new Color(0.85f, 0.30f, 1.0f, 0.98f),
			new Color(1.0f, 0.20f, 0.70f, 0.98f)
		),
		// Palette 3 : Ambre-Or (Orange couchant à Or solaire)
		new BodyPalette(
			"Ambre-Or",
			new Color(1.0f, 0.32f, 0.05f, 0.98f),
			new Color(1.0f, 0.85f, 0.08f, 0.95f),
			new Color(1.0f, 0.50f, 0.08f, 0.98f),
			new Color(1.0f, 0.90f, 0.15f, 0.98f)
		),
		// Palette 4 : Bleu-Glace (Cobalt profond à Bleu glacier arctique)
		new BodyPalette(
			"Bleu-Glace",
			new Color(0.12f, 0.42f, 1.0f, 0.98f),
			new Color(0.55f, 0.92f, 1.0f, 0.95f),
			new Color(0.25f, 0.65f, 1.0f, 0.98f),
			new Color(0.70f, 1.0f, 1.0f, 0.98f)
		),
		// Palette 5 : Lime-Citron (Lime cybernétique à Jaune acide)
		new BodyPalette(
			"Lime-Citron",
			new Color(0.15f, 0.98f, 0.32f, 0.98f),
			new Color(0.95f, 0.98f, 0.08f, 0.95f),
			new Color(0.40f, 1.0f, 0.50f, 0.98f),
			new Color(0.92f, 1.0f, 0.18f, 0.98f)
		),
	};
}

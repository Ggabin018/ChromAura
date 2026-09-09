#nullable enable
using Godot;

/// <summary>
/// Échantillon représentant un point de particule extrait du masque de profondeur Kinect.
/// </summary>
public readonly record struct MaskSample(
	Vector2 Position,
	float NormalizedDepth,
	int PaletteIndex,
	int BodyId
);

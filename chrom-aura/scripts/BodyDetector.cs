#nullable enable
using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Module de détection, segmentation et suivi spatial des corps à partir de l'image de profondeur Kinect.
/// Utilise un algorithme de composantes connexes BFS (8-voisinage) avec seuillage de discontinuité Z,
/// suivi de centroïdes avec hystérésis temporelle pour éviter tout clignotement d'identité ou de palette.
/// </summary>
public sealed class BodyDetector
{
	/// <summary>Liste des corps actuellement suivis.</summary>
	public IReadOnlyList<TrackedBody> TrackedBodies => _trackedBodies;

	/// <summary>Nombre total de corps distincts actuellement suivis.</summary>
	public int DetectedBodyCount => _trackedBodies.Count;

	/// <summary>Profondeur minimale observée dans la dernière trame active (normalisée 0-1).</summary>
	public float MinDepth { get; private set; } = 0.5f;

	/// <summary>Profondeur maximale observée dans la dernière trame active (normalisée 0-1).</summary>
	public float MaxDepth { get; private set; } = 1.0f;

	private readonly List<TrackedBody> _trackedBodies = new();
	private int _nextBodyId;

	// Tampons préalloués pour éviter les allocations mémoire par trame
	private float[] _depthGrid = Array.Empty<float>();
	private int[] _labelGrid = Array.Empty<int>();
	private int[] _bfsQueue = Array.Empty<int>();

	private struct CandidateCluster
	{
		public int Id;
		public int PixelCount;
		public float SumX;
		public float SumY;
		public float MinX;
		public float MaxX;
		public float MinY;
		public float MaxY;
		public float SumDepth;
		public TrackedBody? MatchedBody;
	}

	/// <summary>
	/// Traite une nouvelle image de profondeur, segmente les différents corps par BFS,
	/// met à jour le suivi des centroïdes, et remplit la liste d'échantillons de particules.
	/// </summary>
	public void ProcessDepthImage(
		Image depthImage,
		int clusterStride,
		float maxDepthDiscontinuity,
		int minClusterPixels,
		float maxTrackingDistance,
		int maxMissedFrames,
		int paletteOffset,
		int totalPalettes,
		RandomNumberGenerator random,
		List<MaskSample> outMaskPoints)
	{
		if (depthImage == null)
			throw new ArgumentNullException(nameof(depthImage));

		var width = depthImage.GetWidth();
		var height = depthImage.GetHeight();

		var stride = Mathf.Max(1, clusterStride);
		var gw = width / stride;
		var gh = height / stride;
		var totalCells = gw * gh;

		// Redimensionner les tampons uniquement si la résolution change
		if (_depthGrid.Length < totalCells)
		{
			_depthGrid = new float[totalCells];
			_labelGrid = new int[totalCells];
			_bfsQueue = new int[totalCells];
		}

		// Lecture ultra-rapide des données brutes en mémoire si format RGB8
		var imgFormat = depthImage.GetFormat();
		var rawData = (imgFormat == Image.Format.Rgb8) ? depthImage.GetData() : null;

		var minObserved = 1.0f;
		var maxObserved = 0.0f;
		var activeCount = 0;

		for (var gy = 0; gy < gh; gy++)
		{
			var py = gy * stride;
			var rowOffset = gy * gw;

			for (var gx = 0; gx < gw; gx++)
			{
				var px = gx * stride;
				float depth;

				if (rawData != null)
				{
					var byteIdx = (py * width + px) * 3;
					depth = rawData[byteIdx] / 255.0f;
				}
				else
				{
					depth = depthImage.GetPixel(px, py).R;
				}

				var cellIdx = rowOffset + gx;
				_depthGrid[cellIdx] = depth;
				_labelGrid[cellIdx] = -1;

				if (depth > 0.0f)
				{
					if (depth < minObserved) minObserved = depth;
					if (depth > maxObserved) maxObserved = depth;
					activeCount++;
				}
			}
		}

		if (activeCount > 0)
		{
			MinDepth = minObserved;
			MaxDepth = maxObserved;
		}

		// -------------------------------------------------------------
		// Étape 1 : Segmentation par composantes connexes (BFS 8-voisins)
		// -------------------------------------------------------------
		var candidateClusters = new List<CandidateCluster>();
		for (var gy = 0; gy < gh; gy++)
		{
			for (var gx = 0; gx < gw; gx++)
			{
				var startIdx = gy * gw + gx;
				var startDepth = _depthGrid[startIdx];
				if (startDepth <= 0.0f || _labelGrid[startIdx] != -1)
					continue;

				var clusterIdx = candidateClusters.Count;
				var cluster = new CandidateCluster
				{
					Id = clusterIdx,
					MinX = gx,
					MaxX = gx,
					MinY = gy,
					MaxY = gy,
					SumX = 0,
					SumY = 0,
					SumDepth = 0,
					PixelCount = 0
				};

				var head = 0;
				var tail = 0;
				_bfsQueue[tail++] = startIdx;
				_labelGrid[startIdx] = clusterIdx;

				while (head < tail)
				{
					var curIdx = _bfsQueue[head++];
					var curX = curIdx % gw;
					var curY = curIdx / gw;
					var curDepth = _depthGrid[curIdx];

					cluster.PixelCount++;
					cluster.SumX += curX;
					cluster.SumY += curY;
					cluster.SumDepth += curDepth;
					if (curX < cluster.MinX) cluster.MinX = curX;
					if (curX > cluster.MaxX) cluster.MaxX = curX;
					if (curY < cluster.MinY) cluster.MinY = curY;
					if (curY > cluster.MaxY) cluster.MaxY = curY;

					// Exploration du voisinage à 8 directions (évite la coupure des membres)
					if (curX + 1 < gw) CheckNeighbor(curIdx + 1, curDepth, clusterIdx, ref tail);
					if (curX - 1 >= 0) CheckNeighbor(curIdx - 1, curDepth, clusterIdx, ref tail);
					if (curY + 1 < gh) CheckNeighbor(curIdx + gw, curDepth, clusterIdx, ref tail);
					if (curY - 1 >= 0) CheckNeighbor(curIdx - gw, curDepth, clusterIdx, ref tail);

					if (curX + 1 < gw && curY + 1 < gh) CheckNeighbor(curIdx + gw + 1, curDepth, clusterIdx, ref tail);
					if (curX - 1 >= 0 && curY + 1 < gh) CheckNeighbor(curIdx + gw - 1, curDepth, clusterIdx, ref tail);
					if (curX + 1 < gw && curY - 1 >= 0) CheckNeighbor(curIdx - gw + 1, curDepth, clusterIdx, ref tail);
					if (curX - 1 >= 0 && curY - 1 >= 0) CheckNeighbor(curIdx - gw - 1, curDepth, clusterIdx, ref tail);
				}

				candidateClusters.Add(cluster);
			}
		}

		void CheckNeighbor(int neighborIdx, float currentDepth, int clusterIdx, ref int tail)
		{
			var nDepth = _depthGrid[neighborIdx];
			if (nDepth > 0.0f && _labelGrid[neighborIdx] == -1)
			{
				// Deux pixels voisins appartiennent au même corps s'ils ne présentent pas de falaise de profondeur
				if (Mathf.Abs(currentDepth - nDepth) <= maxDepthDiscontinuity)
				{
					_labelGrid[neighborIdx] = clusterIdx;
					_bfsQueue[tail++] = neighborIdx;
				}
			}
		}

		// -------------------------------------------------------------
		// Étape 2 : Filtrage du bruit capteur et limitation aux N corps majeurs
		// -------------------------------------------------------------
		var validClusters = new List<CandidateCluster>();
		for (var i = 0; i < candidateClusters.Count; i++)
		{
			if (candidateClusters[i].PixelCount >= minClusterPixels)
			{
				validClusters.Add(candidateClusters[i]);
			}
		}

		// Trier par taille décroissante et limiter au nombre maximal de palettes disponibles (5)
		validClusters.Sort((a, b) => b.PixelCount.CompareTo(a.PixelCount));
		if (validClusters.Count > totalPalettes)
		{
			validClusters.RemoveRange(totalPalettes, validClusters.Count - totalPalettes);
		}

		// -------------------------------------------------------------
		// Étape 3 : Suivi temporel persistant (Centroid Tracking)
		// -------------------------------------------------------------
		var matchedTrackedIndices = new HashSet<int>();
		for (var c = 0; c < validClusters.Count; c++)
		{
			var cluster = validClusters[c];
			var centroid = new Vector2(
				(cluster.SumX / cluster.PixelCount) * stride,
				(cluster.SumY / cluster.PixelCount) * stride
			);
			var avgDepth = cluster.SumDepth / cluster.PixelCount;
			var clusterBounds = new Rect2(
				cluster.MinX * stride,
				cluster.MinY * stride,
				(cluster.MaxX - cluster.MinX + 1) * stride,
				(cluster.MaxY - cluster.MinY + 1) * stride
			);

			var bestMatchIndex = -1;
			var bestDistance = float.MaxValue;

			for (var t = 0; t < _trackedBodies.Count; t++)
			{
				if (matchedTrackedIndices.Contains(t))
					continue;

				var body = _trackedBodies[t];
				// Distance combinée 2D (pixels) + profondeur Z
				var dist = centroid.DistanceTo(body.Centroid) + Mathf.Abs(avgDepth - body.AvgDepth) * 150.0f;
				if (dist < maxTrackingDistance && dist < bestDistance)
				{
					bestDistance = dist;
					bestMatchIndex = t;
				}
			}

			if (bestMatchIndex >= 0)
			{
				// Corps existant retrouvé : mise à jour douce du centroïde
				matchedTrackedIndices.Add(bestMatchIndex);
				var body = _trackedBodies[bestMatchIndex];
				body.Centroid = body.Centroid.Lerp(centroid, 0.35f);
				body.BoundingBox = clusterBounds;
				body.AvgDepth = avgDepth;
				body.PixelCount = cluster.PixelCount;
				body.MissedFrames = 0;
				cluster.MatchedBody = body;
			}
			else
			{
				// Nouveau corps arrivant dans le champ : attribution aléatoire d'une palette libre
				var usedPalettes = new HashSet<int>();
				foreach (var b in _trackedBodies)
				{
					usedPalettes.Add(b.PaletteIndex);
				}

				var availablePalettes = new List<int>();
				for (var p = 0; p < totalPalettes; p++)
				{
					if (!usedPalettes.Contains(p))
					{
						availablePalettes.Add(p);
					}
				}

				var newPaletteIndex = availablePalettes.Count > 0
					? availablePalettes[random.RandiRange(0, availablePalettes.Count - 1)]
					: paletteOffset % totalPalettes;

				var newBody = new TrackedBody
				{
					Id = ++_nextBodyId,
					PaletteIndex = newPaletteIndex,
					Centroid = centroid,
					BoundingBox = clusterBounds,
					AvgDepth = avgDepth,
					PixelCount = cluster.PixelCount,
					MissedFrames = 0,
					IsPointing = false
				};
				_trackedBodies.Add(newBody);
				matchedTrackedIndices.Add(_trackedBodies.Count - 1);
				cluster.MatchedBody = newBody;
			}

			validClusters[c] = cluster;
		}

		// Incrémenter le compteur d'absence des corps non détectés sur cette trame
		for (var t = _trackedBodies.Count - 1; t >= 0; t--)
		{
			if (!matchedTrackedIndices.Contains(t))
			{
				_trackedBodies[t].MissedFrames++;
				if (_trackedBodies[t].MissedFrames > maxMissedFrames)
				{
					_trackedBodies.RemoveAt(t);
				}
			}
		}

		// -------------------------------------------------------------
		// Étape 4 : Échantillonnage des points de masque pour les particules
		// -------------------------------------------------------------
		var clusterToBody = new Dictionary<int, TrackedBody>();
		foreach (var cluster in validClusters)
		{
			if (cluster.MatchedBody != null)
			{
				clusterToBody[cluster.Id] = cluster.MatchedBody;
			}
		}

		outMaskPoints.Clear();
		for (var gy = 0; gy < gh; gy++)
		{
			var rowOffset = gy * gw;
			for (var gx = 0; gx < gw; gx++)
			{
				var idx = rowOffset + gx;
				var label = _labelGrid[idx];
				if (label >= 0 && clusterToBody.TryGetValue(label, out var body))
				{
					var depth = _depthGrid[idx];
					var position = new Vector2(
						gx * stride + random.Randf() * stride,
						gy * stride + random.Randf() * stride
					);
					outMaskPoints.Add(new MaskSample(position, depth, body.PaletteIndex, body.Id));
				}
			}
		}

		// Repli de secours si aucun cluster n'a dépassé le seuil de pixels
		if (outMaskPoints.Count == 0 && activeCount > 0)
		{
			for (var gy = 0; gy < gh; gy++)
			{
				var rowOffset = gy * gw;
				for (var gx = 0; gx < gw; gx++)
				{
					var idx = rowOffset + gx;
					var depth = _depthGrid[idx];
					if (depth > 0.0f)
					{
						var position = new Vector2(
							gx * stride + random.Randf() * stride,
							gy * stride + random.Randf() * stride
						);
						outMaskPoints.Add(new MaskSample(position, depth, paletteOffset % totalPalettes, 0));
					}
				}
			}
		}
	}

	/// <summary>
	/// Associe chaque main pointée au corps le plus proche pour lui assigner sa palette de tracé.
	/// </summary>
	public void AssignPointingFingers(
		Godot.Collections.Array<Vector2>? pointingFingers,
		Vector2I maskSize,
		int paletteOffset,
		int totalPalettes,
		List<int> outFingerPaletteIndices)
	{
		outFingerPaletteIndices.Clear();

		foreach (var b in _trackedBodies)
		{
			b.IsPointing = false;
		}

		if (pointingFingers == null || pointingFingers.Count == 0)
			return;

		foreach (var f in pointingFingers)
		{
			var fingerMask = new Vector2(f.X * maskSize.X, f.Y * maskSize.Y);
			var bestPalette = paletteOffset % totalPalettes;
			var bestDist = float.MaxValue;

			foreach (var body in _trackedBodies)
			{
				var d = fingerMask.DistanceTo(body.Centroid);
				if (d < bestDist)
				{
					bestDist = d;
					bestPalette = body.PaletteIndex;
					body.IsPointing = true;
				}
			}

			outFingerPaletteIndices.Add(bestPalette);
		}
	}

	/// <summary>
	/// Retourne une liste de dictionnaires Godot contenant les métadonnées de chaque corps actif pour le débug/HUD.
	/// </summary>
	public Godot.Collections.Array<Godot.Collections.Dictionary> GetBodiesDebugInfo(BodyPalette[] palettes)
	{
		var array = new Godot.Collections.Array<Godot.Collections.Dictionary>();
		foreach (var b in _trackedBodies)
		{
			var pal = palettes[b.PaletteIndex % palettes.Length];
			var dict = new Godot.Collections.Dictionary
			{
				{ "id", b.Id },
				{ "palette_index", b.PaletteIndex },
				{ "palette_name", pal.Name },
				{ "color_near", pal.ColorNear.ToHtml() },
				{ "color_far", pal.ColorFar.ToHtml() },
				{ "centroid_x", b.Centroid.X },
				{ "centroid_y", b.Centroid.Y },
				{ "pixel_count", b.PixelCount },
				{ "is_pointing", b.IsPointing }
			};
			array.Add(dict);
		}
		return array;
	}
}

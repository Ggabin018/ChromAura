# Test MediaPipe Hands

Prototype minimal Godot utilisant GDMP pour afficher la webcam, les landmarks de plusieurs mains et détecter le geste `Pointing_Up` en direct.

## Prérequis

- Godot 4.4 ou plus récent.
- Une webcam accessible par `CameraServer`.

## Lancer

1. Ouvrir ce dossier dans Godot.
2. Vérifier que `Project > Project Settings > Plugins > GDMP` est activé.
3. Lancer le projet avec `F6` ou `F5`.
4. Autoriser Godot à accéder à la caméra si macOS le demande.

Le modèle MediaPipe est inclus dans `assets/models/gesture_recognizer.task`; aucune connexion réseau n'est nécessaire à l'exécution.

## Organisation

- `src/frame_source.gd` : contrat minimal d'une source d'images.
- `src/webcam_frame_source.gd` : capture webcam et conversion YUV vers RGB.
- `src/main.gd` : inférence MediaPipe asynchrone et état de l'interface.
- `src/hand_overlay.gd` : rendu des landmarks au-dessus de la vidéo.
- `src/*.gdshader` : conversion des formats caméra YUV vers RGB.

Les paramètres du modèle, du nombre de mains, du seuil de reconnaissance et de la webcam sont exposés dans l'inspecteur Godot.

Pour remplacer la webcam par un flux Kinect, créer un script dérivé de `FrameSource`, implémenter ses cinq méthodes publiques, puis l'assigner au nœud `FrameSource` de la scène.

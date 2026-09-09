shader_type canvas_item;

// Opaque silhouette drawn from the raw Kinect depth texture, sitting between the
// background waves and the particle systems so the ripples don't show through the
// gaps between the sparse body/trail particles.

uniform vec4 fill_color : source_color = vec4(0.0, 0.0, 0.0, 1.0);
uniform float threshold : hint_range(0.0, 1.0) = 0.65;

void fragment() {
	vec2 uv = UV;
	uv.x = 1.0 - uv.x;
	float depth = texture(TEXTURE, uv).r;
	float alpha = step(threshold, depth);
	COLOR = vec4(fill_color.rgb, alpha * fill_color.a);
}

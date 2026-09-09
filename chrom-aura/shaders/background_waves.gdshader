shader_type canvas_item;

// Ambient background: random water-drop ripples expanding and fading, in shades of blue.

uniform int ripple_count : hint_range(1, 24) = 10;
uniform float ripple_lifetime : hint_range(0.5, 8.0) = 3.2;
uniform float ripple_speed : hint_range(0.05, 1.0) = 0.32;
uniform float ring_sharpness : hint_range(5.0, 80.0) = 26.0;
uniform float intensity : hint_range(0.0, 3.0) = 1.5;
uniform vec4 base_color : source_color = vec4(0.008, 0.015, 0.035, 1.0);
uniform vec4 color_deep : source_color = vec4(0.04, 0.16, 0.5, 1.0);
uniform vec4 color_light : source_color = vec4(0.3, 0.62, 0.95, 1.0);

float hash11(float p) {
	p = fract(p * 0.1031);
	p *= p + 33.33;
	p *= p + p;
	return fract(p);
}

vec2 hash21(float p) {
	vec3 p3 = fract(vec3(p) * vec3(0.1031, 0.1030, 0.0973));
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.xx + p3.yz) * p3.zy);
}

void fragment() {
	vec2 aspect_uv = UV;
	aspect_uv.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;

	vec3 accum = vec3(0.0);

	for (int i = 0; i < ripple_count; i++) {
		float fi = float(i);
		// Stagger each ripple slot with its own random gap so drops don't sync up.
		float gap = 0.5 + hash11(fi * 3.17) * 2.4;
		float period = ripple_lifetime + gap;
		float t_offset = fi * 91.7 + hash11(fi) * 500.0;
		float cycle = floor((TIME + t_offset) / period);
		float seed = fi * 13.37 + cycle * 7.91;

		vec2 origin = hash21(seed);
		origin.x *= SCREEN_PIXEL_SIZE.y / SCREEN_PIXEL_SIZE.x;

		float local_time = mod(TIME + t_offset, period);
		if (local_time > ripple_lifetime) {
			continue;
		}
		float age = local_time / ripple_lifetime;

		float dist = distance(aspect_uv, origin);
		float wave_front = age * ripple_speed;

		// Leading ring plus a fainter trailing ring, like a droplet impact on water.
		float ring = exp(-pow((dist - wave_front) * ring_sharpness, 2.0));
		float ring2 = exp(-pow((dist - wave_front * 0.55) * ring_sharpness * 1.4, 2.0)) * 0.45;
		float fade = pow(1.0 - age, 1.5);
		float amp = (ring + ring2) * fade;

		float hue_t = hash11(seed + 4.2);
		vec3 ripple_color = mix(color_deep.rgb, color_light.rgb, hue_t);

		accum += ripple_color * amp;
	}

	vec3 final_color = base_color.rgb + accum * intensity;
	COLOR = vec4(final_color, 1.0);
}

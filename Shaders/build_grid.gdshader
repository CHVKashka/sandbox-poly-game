shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;

// Сетка построек на плоскости земли (y = 0): тонкие линии по клеткам, жирнее каждый метр, цветные оси X/Z.
uniform float cell_size = 0.25;
uniform int major_every = 4;
uniform float half_extent = 32.0;
uniform float fade_start = 25.0;
uniform float fade_end = 90.0;
uniform vec4 minor_color : source_color = vec4(1.0, 1.0, 1.0, 0.10);
uniform vec4 major_color : source_color = vec4(1.0, 1.0, 1.0, 0.30);
uniform vec4 axis_x_color : source_color = vec4(0.95, 0.30, 0.30, 0.85);
uniform vec4 axis_z_color : source_color = vec4(0.30, 0.50, 1.00, 0.85);

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// Интенсивность линии сетки с периодом `period` (с антиалиасингом по производным).
float grid_line(vec2 coord, float period) {
	vec2 c = coord / period;
	vec2 w = max(fwidth(c), vec2(0.0001));
	vec2 g = abs(fract(c - 0.5) - 0.5) / w;
	return 1.0 - min(min(g.x, g.y), 1.0);
}

float axis_line(float coord, float width_world) {
	float w = max(fwidth(coord), 0.0001);
	return 1.0 - min(abs(coord) / max(w, width_world), 1.0);
}

// Композиция «слой поверх основы» (не premultiplied).
vec4 layer_over(vec4 base, vec3 color, float alpha) {
	float a = alpha + base.a * (1.0 - alpha);
	vec3 rgb = (color * alpha + base.rgb * base.a * (1.0 - alpha)) / max(a, 0.00001);
	return vec4(rgb, a);
}

void fragment() {
	vec2 p = world_pos.xz;
	float dist = length(world_pos - CAMERA_POSITION_WORLD);

	float minor = grid_line(p, cell_size);
	float major = grid_line(p, cell_size * float(major_every));
	float minor_fade = 1.0 - smoothstep(fade_start * 0.4, fade_start, dist);
	float far_fade = 1.0 - smoothstep(fade_start, fade_end, dist);

	vec4 col = vec4(0.0);
	col = layer_over(col, minor_color.rgb, minor * minor_fade * minor_color.a);
	col = layer_over(col, major_color.rgb, major * far_fade * major_color.a);
	col = layer_over(col, axis_x_color.rgb, axis_line(p.y, 0.02) * far_fade * axis_x_color.a);
	col = layer_over(col, axis_z_color.rgb, axis_line(p.x, 0.02) * far_fade * axis_z_color.a);

	float inside = step(abs(p.x), half_extent) * step(abs(p.y), half_extent);
	ALBEDO = col.rgb;
	ALPHA = col.a * inside;
}

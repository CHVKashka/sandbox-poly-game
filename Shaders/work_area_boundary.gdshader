shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_always, blend_mix;

// Пунктирная рамка постоянной толщины в ЭКРАННЫХ пикселях (см. BuildEditor.BuildWorkAreaBoundary): каждое ребро —
// не Mesh.PrimitiveType.Lines (у него нет управляемой ширины на большинстве рендереров), а прямоугольник (2
// треугольника), который здесь же, в вершинном шейдере, растягивается поперёк направления ребра ровно на
// line_width_px пикселей в clip-пространстве — толщина остаётся одинаковой независимо от расстояния до камеры.
// Пунктир — во фрагментном шейдере, по накопленному расстоянию вдоль ребра (UV.x), а не по геометрии.
uniform vec4 line_color : source_color = vec4(0.0, 0.0, 0.0, 0.95);
uniform float line_width_px = 3.0;
uniform float dash_length = 0.6;
uniform float gap_length = 0.4;

varying float v_dist;

void vertex() {
	// NORMAL здесь — не настоящая нормаль (шейдер unshaded, освещение не считается), а единичное направление
	// ребра A->B, общее для всех 4 вершин его прямоугольника (см. BuildWorkAreaBoundary); UV.y — длина ребра —
	// вместе с ним и UV.x (0 у вершин на конце A, длина ребра — на конце B) восстанавливают положение ВТОРОГО
	// конца текущего сегмента, которое нужно для направления линии в экранном пространстве ниже.
	float edge_length = UV.y;
	bool is_a_end = UV.x < 0.0001;
	vec3 other_local = is_a_end ? (VERTEX + NORMAL * edge_length) : (VERTEX - NORMAL * edge_length);
	// COLOR.r здесь — не цвет, а сторона "толщины" линии для этой вершины (0 или 1, см. BuildWorkAreaBoundary) ->
	// -1/+1, куда сдвигать вершину поперёк линии.
	float side = COLOR.r > 0.5 ? 1.0 : -1.0;
	v_dist = UV.x;

	mat4 modelview = VIEW_MATRIX * MODEL_MATRIX;
	vec4 clip_here = PROJECTION_MATRIX * (modelview * vec4(VERTEX, 1.0));
	vec4 clip_other = PROJECTION_MATRIX * (modelview * vec4(other_local, 1.0));

	vec2 screen_here = clip_here.xy / max(abs(clip_here.w), 0.0001);
	vec2 screen_other = clip_other.xy / max(abs(clip_other.w), 0.0001);
	vec2 dir = screen_here - screen_other;
	float dir_len = length(dir);
	vec2 dir_n = dir_len > 0.00001 ? dir / dir_len : vec2(1.0, 0.0);
	vec2 normal_screen = vec2(-dir_n.y, dir_n.x);

	vec2 px_to_ndc = 2.0 / max(VIEWPORT_SIZE, vec2(1.0));
	vec2 offset = normal_screen * (line_width_px * 0.5) * px_to_ndc * side;

	clip_here.xy += offset * clip_here.w;
	POSITION = clip_here;
}

void fragment() {
	float pattern = mod(v_dist, dash_length + gap_length);
	if (pattern > dash_length) discard;

	ALBEDO = line_color.rgb;
	ALPHA = line_color.a;
}

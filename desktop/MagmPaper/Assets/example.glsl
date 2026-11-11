precision mediump float;
uniform float time;
uniform vec2 resolution;
uniform vec2 mouse;

void main() {
    vec2 uv = (gl_FragCoord.xy * 2.0 - resolution) / resolution.y;
    float wave = sin(uv.x * 2.8 + time * 0.35) * 0.3;
    wave += sin(uv.x * 4.0 - time * 0.2) * 0.12;
    float glow = 0.035 / (abs(uv.y - wave) + 0.035);
    vec3 base = vec3(0.025, 0.035, 0.065);
    vec3 color = mix(vec3(0.25, 0.35, 1.0), vec3(0.8, 0.35, 0.65), uv.x * 0.3 + 0.5);
    gl_FragColor = vec4(base + color * glow, 1.0);
}
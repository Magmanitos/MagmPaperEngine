// GLSL Sandbox fragment shader contract: time, resolution, mouse, backbuffer.
// Shared by preview and desktop so both compile and render the same program.
export class ShaderPlayer {
  constructor(canvas, onError = () => {}) {
    this.canvas = canvas;
    this.onError = onError;
    this.gl = canvas.getContext('webgl', { alpha: false, antialias: false, preserveDrawingBuffer: false });
    if (!this.gl) throw new Error('WebGL недоступен. Обновите драйвер видеокарты.');
    this.gl.getExtension('OES_standard_derivatives');
    this.mouse = [0.5, 0.5];
    this.frame = 0;
    this.paused = false;
    this.textures = []; this.buffers = [];
    canvas.addEventListener('webglcontextlost', event => {
      event.preventDefault(); cancelAnimationFrame(this.frame);
      this.onError('Видеодрайвер сбросил WebGL. Откройте шейдер заново или выберите более простой.');
    });
    canvas.addEventListener('pointermove', event => {
      const rect = canvas.getBoundingClientRect();
      this.setMouse((event.clientX - rect.left) / rect.width, 1 - (event.clientY - rect.top) / rect.height);
    });
  }
  setMouse(x, y) {
    if (!Number.isFinite(x) || !Number.isFinite(y)) return;
    this.mouse = [Math.max(0, Math.min(1, x)), Math.max(0, Math.min(1, y))];
  }
  compile(type, source) {
    const gl = this.gl, shader = gl.createShader(type);
    gl.shaderSource(shader, source); gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
      const error = gl.getShaderInfoLog(shader); gl.deleteShader(shader);
      throw new Error(error || 'Ошибка компиляции GLSL.');
    }
    return shader;
  }
  program(source) {
    const gl = this.gl;
    const vertex = this.compile(gl.VERTEX_SHADER, 'attribute vec2 position; varying vec2 surfacePosition; void main(){surfacePosition=position;gl_Position=vec4(position,0.0,1.0);}');
    let fragment, program;
    try {
      fragment = this.compile(gl.FRAGMENT_SHADER, source);
      program = gl.createProgram(); gl.attachShader(program, vertex); gl.attachShader(program, fragment); gl.linkProgram(program);
      if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(program));
      return program;
    } catch (error) { if (program) gl.deleteProgram(program); throw error; }
    finally { gl.deleteShader(vertex); if (fragment) gl.deleteShader(fragment); }
  }
  load(source) {
    const gl = this.gl;
    const program = this.program(source); // Keep the previous program if compilation fails.
    this.dispose();
    this.shader = program;
    this.copy = this.program('precision mediump float; uniform sampler2D image; uniform vec2 resolution; void main(){gl_FragColor=texture2D(image,gl_FragCoord.xy/resolution);}');
    this.quad = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1,1,-1,-1,1,-1,1,1,-1,1,1]), gl.STATIC_DRAW);
    this.uniforms = Object.fromEntries(['time', 'resolution', 'mouse', 'backbuffer'].map(name => [name, gl.getUniformLocation(program, name)]));
    this.copyResolution = gl.getUniformLocation(this.copy, 'resolution');
    this.copyImage = gl.getUniformLocation(this.copy, 'image');
    this.started = performance.now(); this.elapsed = 0; this.last = 0; this.read = 0;
    this.width = 0; this.height = 0;
    this.tick = this.tick.bind(this);
    this.frame = requestAnimationFrame(this.tick);
  }
  resize() {
    const gl = this.gl;
    const ratio = Math.min(window.devicePixelRatio || 1, 2);
    const max = Math.min(3840, gl.getParameter(gl.MAX_TEXTURE_SIZE));
    const w = Math.max(1, Math.min(max, Math.round(this.canvas.clientWidth * ratio)));
    const h = Math.max(1, Math.min(max, Math.round(this.canvas.clientHeight * ratio)));
    if (w === this.width && h === this.height) return;
    this.width = this.canvas.width = w; this.height = this.canvas.height = h;
    for (const texture of this.textures) gl.deleteTexture(texture);
    for (const buffer of this.buffers) gl.deleteFramebuffer(buffer);
    this.textures = []; this.buffers = [];
    for (let i = 0; i < 2; i++) {
      const texture = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      const buffer = gl.createFramebuffer(); gl.bindFramebuffer(gl.FRAMEBUFFER, buffer);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
      this.textures.push(texture); this.buffers.push(buffer);
      if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) !== gl.FRAMEBUFFER_COMPLETE) throw new Error('Недостаточно ресурсов GPU для рендера.');
      gl.clearColor(0, 0, 0, 1); gl.clear(gl.COLOR_BUFFER_BIT);
    }
  }
  draw(program) {
    const gl = this.gl;
    gl.useProgram(program); gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    const location = gl.getAttribLocation(program, 'position');
    gl.enableVertexAttribArray(location); gl.vertexAttribPointer(location, 2, gl.FLOAT, false, 0, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
  }
  tick(now) {
    this.frame = requestAnimationFrame(this.tick);
    if (this.paused || now - this.last < 1000 / 30) return;
    this.last = now;
    try {
      this.resize();
      const gl = this.gl, write = 1 - this.read;
      gl.viewport(0, 0, this.width, this.height);
      gl.bindFramebuffer(gl.FRAMEBUFFER, this.buffers[write]);
      gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, this.textures[this.read]);
      gl.useProgram(this.shader);
      gl.uniform1f(this.uniforms.time, (now - this.started) / 1000);
      gl.uniform2f(this.uniforms.resolution, this.width, this.height);
      gl.uniform2f(this.uniforms.mouse, ...this.mouse);
      gl.uniform1i(this.uniforms.backbuffer, 0);
      this.draw(this.shader);
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.bindTexture(gl.TEXTURE_2D, this.textures[write]);
      gl.useProgram(this.copy); gl.uniform2f(this.copyResolution, this.width, this.height); gl.uniform1i(this.copyImage, 0);
      this.draw(this.copy); this.read = write;
    } catch (error) { cancelAnimationFrame(this.frame); this.onError(error.message); }
  }
  dispose() {
    cancelAnimationFrame(this.frame);
    const gl = this.gl;
    if (this.shader) gl.deleteProgram(this.shader);
    if (this.copy) gl.deleteProgram(this.copy);
    if (this.quad) gl.deleteBuffer(this.quad);
    for (const texture of this.textures) gl.deleteTexture(texture);
    for (const buffer of this.buffers) gl.deleteFramebuffer(buffer);
    this.textures = []; this.buffers = []; this.shader = null; this.copy = null; this.quad = null;
  }
}

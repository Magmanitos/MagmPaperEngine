// GLSL Sandbox fragment shader contract: time, resolution, mouse, backbuffer.
// Shared by preview and desktop so both compile and render the same program.
import { FrameClock, MouseMotion } from './motion.mjs';
export class ShaderPlayer {
  constructor(canvas, onError = () => {}, { smoothMouse = false } = {}) {
    this.canvas = canvas;
    this.onError = onError;
    this.gl = canvas.getContext('webgl', { alpha: false, antialias: false, preserveDrawingBuffer: false });
    if (!this.gl) throw new Error('WebGL недоступен. Обновите драйвер видеокарты.');
    this.gl.getExtension('OES_standard_derivatives');
    this.maxTextureSize = Math.min(3840, this.gl.getParameter(this.gl.MAX_TEXTURE_SIZE));
    this.motion = new MouseMotion(smoothMouse ? 20 : 0);
    this.frame = 0;
    this._paused = false;
    this.fps = 60; this.renderScale = 1;
    this.tick = this.tick.bind(this);
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
    this.motion.setTarget(x, y);
  }
  setMouseEnabled(enabled) { this.motion.setEnabled(enabled); }
  get paused() { return this._paused; }
  set paused(value) {
    value = !!value;
    if (value === this._paused) return;
    this._paused = value;
    if (value) {
      this.pauseStarted = performance.now();
      cancelAnimationFrame(this.frame); this.frame = 0;
    } else if (this.shader) {
      this.started += performance.now() - this.pauseStarted;
      this.clock = new FrameClock(this.fps);
      this.frame = requestAnimationFrame(this.tick);
    }
  }
  configure({ fps = this.fps, renderScale = this.renderScale }) {
    if (![15, 30, 60].includes(fps) || ![0.5, 0.75, 1].includes(renderScale)) throw new Error('Некорректные параметры рендера.');
    if (fps !== this.fps) { this.fps = fps; this.clock = new FrameClock(fps); }
    this.renderScale = renderScale;
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
    this.usesBackbuffer = gl.getUniformLocation(program, 'backbuffer') !== null;
    this.copy = this.usesBackbuffer ? this.program('precision mediump float; uniform sampler2D image; uniform vec2 resolution; void main(){gl_FragColor=texture2D(image,gl_FragCoord.xy/resolution);}') : null;
    this.quad = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, this.quad);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1,1,-1,-1,1,-1,1,1,-1,1,1]), gl.STATIC_DRAW);
    this.uniforms = Object.fromEntries(['time', 'resolution', 'mouse', 'backbuffer'].map(name => [name, gl.getUniformLocation(program, name)]));
    this.copyResolution = this.copy ? gl.getUniformLocation(this.copy, 'resolution') : null;
    this.copyImage = this.copy ? gl.getUniformLocation(this.copy, 'image') : null;
    this.started = performance.now(); this.clock = new FrameClock(this.fps); this.read = 0;
    if (this.paused) this.pauseStarted = this.started;
    this.width = 0; this.height = 0;
    if (!this.paused) this.frame = requestAnimationFrame(this.tick);
  }
  resize() {
    const gl = this.gl;
    const ratio = Math.min(window.devicePixelRatio || 1, 2) * this.renderScale;
    const max = this.maxTextureSize;
    const scale = Math.min(ratio, max / Math.max(1, this.canvas.clientWidth), max / Math.max(1, this.canvas.clientHeight));
    const w = Math.max(1, Math.round(this.canvas.clientWidth * scale));
    const h = Math.max(1, Math.round(this.canvas.clientHeight * scale));
    if (w === this.width && h === this.height) return;
    this.width = this.canvas.width = w; this.height = this.canvas.height = h;
    for (const texture of this.textures) gl.deleteTexture(texture);
    for (const buffer of this.buffers) gl.deleteFramebuffer(buffer);
    this.textures = []; this.buffers = [];
    if (!this.usesBackbuffer) return;
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
    if (this.paused || !this.shader) { this.frame = 0; return; }
    this.frame = requestAnimationFrame(this.tick);
    const elapsed = this.clock.advance(now);
    if (elapsed === null) return;
    try {
      this.resize();
      const gl = this.gl, write = 1 - this.read;
      gl.viewport(0, 0, this.width, this.height);
      gl.bindFramebuffer(gl.FRAMEBUFFER, this.usesBackbuffer ? this.buffers[write] : null);
      if (this.usesBackbuffer) {
        gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, this.textures[this.read]);
      }
      gl.useProgram(this.shader);
      gl.uniform1f(this.uniforms.time, (now - this.started) / 1000);
      gl.uniform2f(this.uniforms.resolution, this.width, this.height);
      gl.uniform2f(this.uniforms.mouse, ...this.motion.advance(elapsed));
      gl.uniform1i(this.uniforms.backbuffer, 0);
      this.draw(this.shader);
      if (!this.usesBackbuffer) return; // Avoid a full-screen copy pass for ordinary shaders.
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
      gl.bindTexture(gl.TEXTURE_2D, this.textures[write]);
      gl.useProgram(this.copy); gl.uniform2f(this.copyResolution, this.width, this.height); gl.uniform1i(this.copyImage, 0);
      this.draw(this.copy); this.read = write;
    } catch (error) { cancelAnimationFrame(this.frame); this.onError(error.message); }
  }
  dispose() {
    cancelAnimationFrame(this.frame);
    this.frame = 0;
    const gl = this.gl;
    if (this.shader) gl.deleteProgram(this.shader);
    if (this.copy) gl.deleteProgram(this.copy);
    if (this.quad) gl.deleteBuffer(this.quad);
    for (const texture of this.textures) gl.deleteTexture(texture);
    for (const buffer of this.buffers) gl.deleteFramebuffer(buffer);
    this.textures = []; this.buffers = []; this.shader = null; this.copy = null; this.quad = null;
  }
}

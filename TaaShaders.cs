namespace Marionette.Desktop;

public static class TaaShaders
{
    public const string Vertex = """
        #version 460 core
        out vec2 uv;
        void main() {
            uv = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
            gl_Position = vec4(uv * 2.0 - 1.0, 0, 1);
        }
        """;
    public const string Fragment = """
        #version 460 core
        in vec2 uv;
        out vec4 result;
        uniform sampler2D uCurrent, uDepth, uHistory, uHistoryDepth;
        uniform mat4 uInverseCurrent, uPrevious;
        uniform bool uValid;
        uniform vec2 uTexel;
        uniform float uVarianceGamma, uHistoryWeight;
        uniform sampler2D uVelocity, uVelocityDepth;
        uniform bool uHasVelocity;
        uniform vec2 uCurrentJitter, uPreviousJitter;
        vec3 toYCoCg(vec3 rgb) {
            return vec3(dot(rgb,vec3(0.25,0.5,0.25)), 0.5*(rgb.r-rgb.b), 0.5*rgb.g-0.25*(rgb.r+rgb.b));
        }
        vec3 fromYCoCg(vec3 c) { return vec3(c.x+c.y-c.z,c.x+c.z,c.x-c.y-c.z); }
        void main() {
            // Resolve onto an unjittered output grid. Keeping the result in raster
            // jitter coordinates makes even converged static silhouettes oscillate.
            vec2 rasterUv = uv + uCurrentJitter;
            vec3 current = texture(uCurrent, rasterUv).rgb;
            result = vec4(current, 1);
            float depth = texture(uDepth, rasterUv).r;
            vec4 world = uInverseCurrent * vec4(rasterUv*2.0-1.0, depth*2.0-1.0, 1);
            world /= abs(world.w) > 1e-8 ? world.w : 1.0;
            vec4 previous = uPrevious * world;
            vec3 projected = previous.xyz / (abs(previous.w)>1e-8 ? previous.w : 1.0) * 0.5 + 0.5;
            projected.xy -= uPreviousJitter;
            vec4 velocity = texture(uVelocity,rasterUv);
            if (uHasVelocity && depth < 0.999999) projected=vec3(uv-velocity.xy*uTexel,velocity.z);
            // Derivatives must be evaluated before divergent early returns.
            float tolerance = max(0.00015, 2.0 * (abs(dFdx(projected.z))+abs(dFdy(projected.z))));
            if (!uValid || previous.w <= 0) return;
            if (uHasVelocity && depth < 0.999999 && (velocity.w < 0.99 || abs(texture(uVelocityDepth,rasterUv).r-depth)>0.0002)) return;
            if (any(lessThan(projected,vec3(0))) || any(greaterThan(projected,vec3(1)))) return;
            float oldDepth = texture(uHistoryDepth,projected.xy+uPreviousJitter).r;
            if (abs(oldDepth-projected.z) > tolerance) return;
            vec3 center=toYCoCg(current);
            vec3 mean=vec3(0), second=vec3(0), lo=center, hi=center;
            for (int y=-1;y<=1;y++) for (int x=-1;x<=1;x++) {
                vec3 c=toYCoCg(texture(uCurrent,rasterUv+vec2(x,y)*uTexel).rgb);
                mean+=c; second+=c*c; lo=min(lo,c); hi=max(hi,c);
            }
            mean/=9.0;
            vec3 sigma=sqrt(max(second/9.0-mean*mean,vec3(0)));
            vec3 old=toYCoCg(texture(uHistory,projected.xy).rgb);
            if (any(isnan(old)) || any(isinf(old))) return;
            old=clamp(old,max(lo,mean-uVarianceGamma*sigma),min(hi,mean+uVarianceGamma*sigma));
            float motion=length((uv-projected.xy)/uTexel);
            float weight=mix(uHistoryWeight,min(0.2,uHistoryWeight),clamp(motion/16.0,0.0,1.0));
            result=vec4(max(mix(current,fromYCoCg(old),weight),vec3(0)),1);
        }
        """;
}

#version 330 core
out vec3 FragColor;

in vec2 TexCoords;

#define MAX_REFLECTION_LOD 4

// 160
layout (std140) uniform Camera {
	mat4 view;			// 64	0	64
	mat4 projection;	// 64	64	64
	vec3 viewPos;		// 12	128	144
	float nearPlane;	// 4	144	148
	float farPlane;		// 4	148	152
	float fov;			// 4	152	156
	float ar;			// 4	156	160
};

// 32
layout (std140) uniform Environment {
	vec3 colorFactor;     // 12     0
	float intensity;      //  4    12
	bool haveSkybox;      //  4    16
	bool haveEnvironment; //  4    20
};

uniform sampler2D GBuffer0;
uniform sampler2D GBuffer1;
uniform sampler2D GBuffer2;
uniform sampler2D GBuffer3;

uniform samplerCube irradianceMap;
uniform samplerCube prefilterMap;
uniform sampler2D   brdfLUT;

vec3 GetF0(vec3 albedo, float metallic);
vec3 FrR(vec3 wo, vec3 norm, vec3 F0, float roughness);

// We have a better approximation of the off specular peak
// but due to the other approximations we found this one performs better.
// N is the normal direction
// R is the mirror vector
// This approximation works fine for G smith correlated and uncorrelated
vec3 getSpecularDominantDir(vec3 norm, vec3 R, float roughness);

// N is the normal direction
// V is the view vector
// NdotV is the cosine angle between the view vector and the normal
vec3 getDiffuseDominantDir(vec3 N, vec3 V, float roughness);

vec3 EvaluateAmbient(int ID, vec3 wo, vec3 norm, vec3 albedo, float roughness, float metallic, float ao);
vec3 EvaluateAmbient_MetalWorkflow(vec3 wo, vec3 norm, vec3 albedo, float roughness, float metallic, float ao);
vec3 EvaluateAmbient_Diffuse(vec3 norm, vec3 albedo);
vec3 EvaluateAmbient_Frostbite(vec3 wo, vec3 norm, vec3 albedo, float roughness, float metallic, float ao);

void main() {
	if( !haveEnvironment ) {
		FragColor = vec3(0, 0, 0);
		return;
	}
	
	// prepare
	vec4 data0 = texture(GBuffer0, TexCoords);
	vec4 data1 = texture(GBuffer1, TexCoords);
	vec4 data2 = texture(GBuffer2, TexCoords);
	vec4 data3 = texture(GBuffer3, TexCoords);
	
	int ID = int(data0.w);
	vec3 pos = data0.xyz;
	vec3 norm = data1.xyz;
	vec3 albedo = data2.xyz;
	float roughness = data1.w;
	float metallic = data2.w;
	float ao = data3.w;
	
	vec3 wo = normalize(viewPos - pos);
	
	vec3 ambient = EvaluateAmbient(ID, wo, norm, albedo, roughness, metallic, ao);
	
	FragColor = ambient;
}

vec3 GetF0(vec3 albedo, float metallic) {
    return mix(vec3(0.04), albedo, metallic);
}

vec3 FrR(vec3 wo, vec3 norm, vec3 F0, float roughness) {
	float cosTheta = clamp(dot(wo, norm), 0, 1);
	vec3 lambda = max(vec3(1 - roughness), F0);
    return F0 + (lambda - F0) * pow(1 - cosTheta, 5);
}

vec3 getSpecularDominantDir(vec3 norm, vec3 R, float roughness) {
    float smoothness = clamp(1 - roughness, 0, 1);
    float lerpFactor = smoothness * (sqrt(smoothness) + roughness);
    // The result is not normalized as we fetch in a cubemap
    return mix(norm, R, lerpFactor);
}

vec3 getDiffuseDominantDir(vec3 N, vec3 V, float roughness) {
	float NoV = dot(N, V);
	float a = 1.02341f * roughness - 1.51174f;
	float b = -0.511705f * roughness + 0.755868f;
	float lerpFactor = clamp((NoV * a + b) * roughness, 0, 1);
	// The result is not normalized as we fetch in a cubemap
	return mix(N, V, lerpFactor);
}

vec3 EvaluateAmbient(int ID, vec3 wo, vec3 norm, vec3 albedo, float roughness, float metallic, float ao) {
	if(ID == 0) {
		return EvaluateAmbient_MetalWorkflow(wo, norm, albedo, roughness, metallic, ao);
	}
	else if(ID == 1) {
		return EvaluateAmbient_Diffuse(norm, albedo);
	}
	else if(ID == 2) {
		return EvaluateAmbient_Frostbite(wo, norm, albedo, roughness, metallic, ao);
	}
	else
		return vec3(0); // not support ID
}

vec3 EvaluateAmbient_MetalWorkflow(vec3 wo, vec3 norm, vec3 albedo, float roughness, float metallic, float ao) {
	vec3 F0 = GetF0(albedo, metallic);
	vec3 F = FrR(wo, norm, F0, roughness);
	vec3 kS = F;
	vec3 kD = (1 - metallic) * (vec3(1) - kS);
	
	// diffuse
	vec3 irradiance = haveSkybox ? texture(irradianceMap, norm).rgb : vec3(1);
	vec3 diffuse = irradiance * albedo;
	
	// specular
	vec3 R = reflect(-wo, norm);
	// 用 R 采样
	vec3 prefilteredColor = haveSkybox ? textureLod(prefilterMap, R,  roughness * MAX_REFLECTION_LOD).rgb : vec3(1);
	float cosTheta = clamp(dot(norm, wo), 0, 1);
	vec2 envBRDF = texture(brdfLUT, vec2(cosTheta, roughness)).rg;
	vec3 specular = prefilteredColor * (F0 * envBRDF.x + envBRDF.y);
	
	return (kD * diffuse + specular) * ao * intensity * colorFactor;
}

vec3 EvaluateAmbient_Diffuse(vec3 norm, vec3 albedo) {
	vec3 irradiance = haveSkybox ? texture(irradianceMap, norm).rgb : vec3(1);
	return irradiance * albedo * intensity * colorFactor;
}

vec3 EvaluateAmbient_Frostbite(vec3 wo, vec3 norm, vec3 albedo, float roughness, float metallic, float ao) {
	float perpRoughness = roughness * roughness;
	float cosTheta = clamp(dot(norm, wo), 0, 1);
	vec3 envBRDF = texture(brdfLUT, vec2(cosTheta, perpRoughness)).rgb;
	
	// diffuse, need update to Disney Diffuse
	vec3 dominantN = getDiffuseDominantDir(norm, wo, perpRoughness);
	vec3 irradiance = haveSkybox ? texture(irradianceMap, dominantN).rgb : vec3(1);
	vec3 diffuse = irradiance * envBRDF.z * albedo;
	
	// specular
	vec3 R = reflect(-wo, norm);
	vec3 dominantDir = getSpecularDominantDir(norm, R, perpRoughness);
	// 用 dominantDir 采样
	vec3 prefilteredColor = haveSkybox ? textureLod(prefilterMap, dominantDir,  perpRoughness * MAX_REFLECTION_LOD).rgb : vec3(1);
	vec3 F0 = GetF0(albedo, metallic);
	vec3 specular = prefilteredColor * (F0 * envBRDF.x + envBRDF.y);
	
	return ((1 - metallic) * diffuse + specular) * ao * intensity * colorFactor;
}

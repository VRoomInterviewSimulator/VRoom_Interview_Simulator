import os
import re
import json
import asyncio
import torch
import numpy as np
import uvicorn
import librosa
from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel
from openai import OpenAI
from dotenv import load_dotenv
from threading import Lock

from fish_speech.models.vqgan.modules.firefly import FireflyArchitecture
from fish_speech.models.text2semantic.llama import DualARTransformer
from fish_speech.models.text2semantic.inference import generate_long, decode_one_token_ar
from fish_speech.models.vqgan.inference import load_model as load_vqgan

# 환경 변수 로드
load_dotenv()

app = FastAPI()
tts_lock = Lock()

device = "cuda" if torch.cuda.is_available() else "cpu"

# 정밀도 설정
if torch.cuda.is_available():
    torch.backends.cuda.matmul.allow_tf32 = True
    torch.backends.cudnn.allow_tf32 = True
    precision = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
else:
    precision = torch.float32

print(f"[Init] Loading Fish Speech 1.5 models on {device} with {precision}...")

base_dir = os.path.dirname(os.path.abspath(__file__))
fs_model_path = os.path.join(base_dir, "model", "fish-speech-1.5")

# VQGAN 로드
print("[Init] Loading VQGAN...")
vqgan_model = load_vqgan(   
    config_name="firefly_gan_vq",
    checkpoint_path=f"{fs_model_path}/firefly-gan-vq-fsq-8x1024-21hz-generator.pth",
    device=device,
)
vqgan_model.eval()

# LLaMA 로드
print("[Init] Loading LLaMA Text2Semantic model...")
llama_model = DualARTransformer.from_pretrained(fs_model_path, load_weights=True)
llama_model.to(device=device, dtype=precision)
llama_model.eval()

# KV 캐시 초기화
print("[Init] Setting up LLaMA caches...")
with torch.device(device):
    llama_model.setup_caches(
        max_batch_size=1,
        max_seq_len=llama_model.config.max_seq_len,
        dtype=precision,
    )

# 컴파일 최적화
print("[Init] Compiling decode function for extreme speed...")
import torch._inductor.config
torch._inductor.config.coordinate_descent_tuning = True
torch._inductor.config.triton.unique_kernel_names = True

compiled_decode = torch.compile(
    decode_one_token_ar,
    fullgraph=True,
    backend="inductor",
    mode="max-autotune"
)

speaker_wav_path = os.path.join(base_dir, "speaker.wav")
prompt_tokens = None
prompt_texts = os.environ.get("REFERENCE_DIALOGUE", "면접을 시작합니다.")

# 안정적인 librosa 기반 로딩
if os.path.exists(speaker_wav_path):
    print(f"[Init] Pre-computing prompt tokens with librosa...")
    audio_data, sr = librosa.load(speaker_wav_path, sr=vqgan_model.spec_transform.sample_rate)
    audio = torch.from_numpy(audio_data).float()
    
    if audio.dim() == 1:
        audio = audio.unsqueeze(0).unsqueeze(0)
    elif audio.dim() == 2:
        audio = audio.unsqueeze(0)

    audio = audio.to(device=device)
    audio_lengths = torch.tensor([audio.shape[-1]], device=device)

    with torch.inference_mode():
        indices, _ = vqgan_model.encode(audio, audio_lengths)
        prompt_tokens = indices[0]
else:
    print(f"[Warning] speaker.wav not found. Using default voice.")

if device == "cuda":
    torch.cuda.empty_cache()

print("[Init] Fish Speech 1.5 models loaded and ready.")

client = OpenAI(api_key=os.environ.get("GROQ_API_KEY"), base_url="https://api.groq.com/openai/v1")

class TTSRequest(BaseModel):
    text: str

def synthesize_audio(text: str):
    with tts_lock:
        try:
            with torch.inference_mode():
                token_generator = generate_long(
                    model=llama_model,
                    device=device,
                    text=text,
                    prompt_text=prompt_texts[0] if prompt_tokens is not None else None,
                    prompt_tokens=prompt_tokens,
                    max_new_tokens=1024,
                    temperature=0.7,
                    decode_one_token=compiled_decode
                )

                all_codes = []
                for response in token_generator:
                    if response.action == "sample":
                        all_codes.append(response.codes)

                if not all_codes: return b""

                acoustic_tokens = torch.cat(all_codes, dim=-1).unsqueeze(0).to(device)
                feature_lengths = torch.tensor([acoustic_tokens.shape[-1]], device=device)

                audio_tensor, _ = vqgan_model.decode(indices=acoustic_tokens, feature_lengths=feature_lengths)
                return audio_tensor.squeeze().cpu().float().numpy().astype(np.float32).tobytes()
        except Exception as e:
            print(f"[Error] Synthesis failed: {e}")
            return b""

async def response_generator(user_text: str):
    try:
        response = client.chat.completions.create(
            model=os.environ.get("MODEL_NAME"),
            messages=[{"role": "system", "content": os.environ.get("SYSTEM_PROMPT")}, {"role": "user", "content": user_text}],
            stream=True
        )
        sentence_buffer = ""
        for chunk in response:
            if not chunk.choices: continue
            content = chunk.choices[0].delta.content
            if content:
                sentence_buffer += content
                if any(punc in content for punc in [".", "?", "!", "\n"]):
                    sentences = re.split(r'(?<=[.?!])\s+', sentence_buffer)
                    for i in range(len(sentences) - 1):
                        sentence = sentences[i].strip()
                        if sentence:
                            audio_chunk = await asyncio.to_thread(synthesize_audio, sentence)
                            if audio_chunk: yield audio_chunk
                    sentence_buffer = sentences[-1]
        if sentence_buffer.strip():
            audio_chunk = await asyncio.to_thread(synthesize_audio, sentence_buffer.strip())
            if audio_chunk: yield audio_chunk
    except Exception as e:
        print(f"[Error] response_generator failed: {e}")

@app.post("/process")
async def process_text_to_audio(request: TTSRequest):
    return StreamingResponse(response_generator(request.text), media_type="application/octet-stream")

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=8001)
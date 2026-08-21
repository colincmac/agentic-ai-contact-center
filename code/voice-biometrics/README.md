\# Voice Biometrics Service



A Python gRPC microservice for voice biometric enrollment and verification using SpeechBrain's ECAPA-TDNN speaker recognition model.



\## Features



\- \*\*Speaker Enrollment\*\*: Enroll users by streaming their voice samples

\- \*\*Speaker Verification\*\*: Verify users against their enrolled voice profile

\- \*\*Streaming Audio Support\*\*: Client-streaming RPC for efficient audio transmission

\- \*\*Configurable Threshold\*\*: Adjust similarity threshold via environment variables



\## Requirements



\- Python 3.12+

\- gRPC

\- PyTorch

\- SpeechBrain

\- libsndfile1 (for audio processing)



\## Quick Start



\### Using Docker



```bash

\# Build the image

docker build -t voice-biometrics .



\# Run the container

docker run -p 50051:50051 -v embeddings:/app/embeddings voice-biometrics

```



\### Local Development



```bash

\# Install dependencies

pip install -r requirements.txt



\# Generate proto stubs

python -m grpc\_tools.protoc -I./protos --python\_out=. --grpc\_python\_out=. ./protos/biometrics.proto



\# Run the server

python server.py

```



\## Configuration



Environment variables:



| Variable | Default | Description |

|----------|---------|-------------|

| `GRPC\_PORT` | `50051` | gRPC server port |

| `HTTP_HEALTH\_PORT` | `8080` | HTTP health check port (`/health`, `/healthz`) |

| `EMBEDDINGS\_DIR` | `./embeddings` | Directory to store user embeddings |

| `SIMILARITY\_THRESHOLD` | `0.25` | Threshold for speaker verification match |



\## API



\### EnrollUser (Client Streaming)



Enroll a user with their voice sample.



\*\*Request Stream:\*\*

1\. First message: `user\_id` (string) - User identifier

2\. Subsequent messages: `audio\_chunk` (bytes) - 16kHz mono 16-bit PCM audio



\*\*Response:\*\*

\- `success` (bool) - Whether enrollment succeeded

\- `message` (string) - Status message



\### VerifyUser (Client Streaming)



Verify a user against their enrolled voice sample.



\*\*Request Stream:\*\*

1\. First message: `user\_id` (string) - User identifier to verify against

2\. Subsequent messages: `audio\_chunk` (bytes) - 16kHz mono 16-bit PCM audio



\*\*Response:\*\*

\- `is\_match` (bool) - Whether the speaker matches

\- `similarity\_score` (float) - Cosine similarity score between embeddings



\## Audio Format



The service expects:

\- Sample rate: 16kHz

\- Channels: Mono

\- Bit depth: 16-bit signed PCM

\- Format: Raw bytes



\## Security Considerations



⚠️ \*\*Production Deployment:\*\*

\- The default server uses insecure gRPC connections

\- For production, configure TLS/SSL using `add\_secure\_port()` with proper certificates

\- Consider network-level security (VPN, private networks)

\- User IDs are sanitized but additional validation may be needed for your use case



\## Model



This service uses the \[SpeechBrain ECAPA-TDNN](https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb) model trained on VoxCeleb. The model is automatically downloaded on first run.



\## License



See repository root for license information.


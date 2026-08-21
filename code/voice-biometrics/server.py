"""
Voice Biometrics gRPC Server

This module implements a gRPC server for voice biometric enrollment and verification
using SpeechBrain's ECAPA-TDNN speaker recognition model.
"""

import os
import sys
import logging
import threading
from concurrent import futures
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from abc import ABC, abstractmethod
from typing import Optional

import grpc
import numpy as np
import torch
from grpc_health.v1 import health
from grpc_health.v1 import health_pb2
from grpc_health.v1 import health_pb2_grpc
from speechbrain.inference.speaker import SpeakerRecognition
from models import biometrics_pb2, biometrics_pb2_grpc

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

# Configuration
EMBEDDINGS_DIR = Path(os.getenv("EMBEDDINGS_DIR", "./embeddings"))

SAMPLE_RATE = 16000  # Expected sample rate for the model
SIMILARITY_THRESHOLD = float(os.getenv("SIMILARITY_THRESHOLD", "0.25"))
GRPC_PORT = int(os.getenv("GRPC_PORT", "50051"))
HTTP_HEALTH_PORT = int(os.getenv("HTTP_HEALTH_PORT", "8080"))
MIN_AUDIO_DURATION_SECONDS = float(os.getenv("MIN_AUDIO_DURATION_SECONDS", "1.0"))
MAX_AUDIO_DURATION_SECONDS = float(os.getenv("MAX_AUDIO_DURATION_SECONDS", "30.0"))


class EmbeddingStore(ABC):
    """Abstract storage for user embeddings."""

    @abstractmethod
    def save(self, user_id: str, embedding: torch.Tensor) -> None:
        """Persist an embedding for the given user_id."""

    @abstractmethod
    def load(self, user_id: str) -> torch.Tensor:
        """Load a previously stored embedding for the given user_id.

        Raises FileNotFoundError if no embedding exists for the user.
        """

    @abstractmethod
    def exists(self, user_id: str) -> bool:
        """Return True if an embedding exists for the given user_id."""


class FileEmbeddingStore(EmbeddingStore):
    """File-system based embedding storage.

    This implementation can later be swapped for Azure Blob Storage,
    Cosmos DB, or other backends without changing service logic.
    """

    def __init__(self, base_dir: Path = EMBEDDINGS_DIR) -> None:
        self.base_dir = base_dir
        self.base_dir.mkdir(parents=True, exist_ok=True)

    def _get_embedding_path(self, user_id: str) -> Path:
        """Get the file path for a user's embedding with safe user_id handling."""
        safe_user_id = "".join(c for c in user_id if c.isalnum() or c in "-_")
        if not safe_user_id or len(safe_user_id) < 1:
            raise ValueError("Invalid user_id: must contain at least one alphanumeric character")
        return self.base_dir / f"{safe_user_id}.pt"

    def save(self, user_id: str, embedding: torch.Tensor) -> None:
        path = self._get_embedding_path(user_id)
        temp_path = path.with_suffix(path.suffix + ".tmp")
        # Atomic write: write to temp file then replace
        torch.save(embedding, temp_path)
        os.replace(temp_path, path)

    def load(self, user_id: str) -> torch.Tensor:
        path = self._get_embedding_path(user_id)
        if not path.exists():
            raise FileNotFoundError(f"No embedding found for user_id '{user_id}'")

        embedding = torch.load(path, map_location="cpu")
        if not isinstance(embedding, torch.Tensor):
            raise TypeError("Stored embedding is not a torch.Tensor")
        return embedding.cpu()

    def exists(self, user_id: str) -> bool:
        path = self._get_embedding_path(user_id)
        return path.exists()


class BiometricServiceServicer(biometrics_pb2_grpc.BiometricServiceServicer):
    """gRPC servicer for voice biometric operations."""

    def __init__(self, embedding_store: Optional[EmbeddingStore] = None):
        """Initialize the service with the speaker recognition model."""
        logger.info("Loading SpeechBrain speaker recognition model...")
        self.device = "cuda" if torch.cuda.is_available() else "cpu"
        logger.info(f"Using device: {self.device}")

        self.model = SpeakerRecognition.from_hparams(
            source="speechbrain/spkrec-ecapa-voxceleb",
            savedir="pretrained_models/spkrec-ecapa-voxceleb",
            run_opts={"device": self.device},
        )
        logger.info("Model loaded successfully")

        # Set up embedding storage (filesystem by default, but can be swapped out)
        self.embedding_store: EmbeddingStore = embedding_store or FileEmbeddingStore()
        if isinstance(self.embedding_store, FileEmbeddingStore):
            logger.info(f"Embeddings directory: {self.embedding_store.base_dir.absolute()}")

    def _bytes_to_waveform(self, audio_bytes: bytes) -> torch.Tensor:
        """
        Convert raw audio bytes to a waveform tensor.

        Assumes 16kHz mono 16-bit PCM audio.

        Args:
            audio_bytes: Raw audio bytes (16-bit PCM)

        Returns:
            Waveform tensor suitable for the model
        """
        # Convert bytes to numpy array (16-bit signed integers)
        audio_np = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)

        if audio_np.size == 0:
            raise ValueError("No audio data provided")

        # Basic duration sanity checks
        duration_seconds = audio_np.size / float(SAMPLE_RATE)
        if duration_seconds < MIN_AUDIO_DURATION_SECONDS:
            raise ValueError(
                f"Audio too short: {duration_seconds:.2f}s (minimum {MIN_AUDIO_DURATION_SECONDS:.2f}s)"
            )
        if duration_seconds > MAX_AUDIO_DURATION_SECONDS:
            raise ValueError(
                f"Audio too long: {duration_seconds:.2f}s (maximum {MAX_AUDIO_DURATION_SECONDS:.2f}s)"
            )

        # Normalize to [-1, 1] range
        audio_np = audio_np / 32768.0

        # Convert to torch tensor and add batch dimension
        waveform = torch.from_numpy(audio_np).unsqueeze(0)

        return waveform

    def _compute_embedding(self, waveform: torch.Tensor) -> torch.Tensor:
        """
        Compute speaker embedding from waveform.

        Args:
            waveform: Audio waveform tensor

        Returns:
            Speaker embedding tensor
        """
        if self.model is None:
            raise RuntimeError("SpeakerRecognition model is not loaded (self.model is None)")
        with torch.no_grad():
            embedding = self.model.encode_batch(waveform.to(self.device))
        return embedding.squeeze().cpu()


    def EnrollUser(self, request_iterator, context):
        """
        Enroll a user with their voice sample.

        The first message should contain the user_id, followed by
        messages containing audio chunks.
        """
        user_id = None
        audio_chunks = []

        try:
            for request in request_iterator:
                data_type = request.WhichOneof("data")

                if data_type == "user_id":
                    if user_id is not None:
                        logger.warning("Received multiple user_id messages")
                    user_id = request.user_id
                    logger.info(f"Enrolling user: {user_id}")
                elif data_type == "audio_chunk":
                    audio_chunks.append(request.audio_chunk)
                else:
                    logger.warning(f"Received empty message")

            if user_id is None:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("No user_id provided in the stream")
                return biometrics_pb2.EnrollResponse(
                    success=False,
                    message="No user_id provided in the stream",
                )

            if not audio_chunks:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("No audio data provided in the stream")
                return biometrics_pb2.EnrollResponse(
                    success=False,
                    message="No audio data provided in the stream",
                )

            # Combine all audio chunks
            combined_audio = b"".join(audio_chunks)
            logger.info(f"Received {len(combined_audio)} bytes of audio for user {user_id}")

            # Convert to waveform
            waveform = self._bytes_to_waveform(combined_audio)

            # Compute embedding
            embedding = self._compute_embedding(waveform)

            # Save embedding via configured storage backend
            self.embedding_store.save(user_id, embedding)
            logger.info(f"Saved embedding for user {user_id}")

            return biometrics_pb2.EnrollResponse(
                success=True,
                message=f"Successfully enrolled user {user_id}"
            )

        except ValueError as e:
            logger.error(f"Validation error during enrollment: {e}", exc_info=True)
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(str(e))
            return biometrics_pb2.EnrollResponse(
                success=False,
                message=f"Enrollment failed: {str(e)}",
            )
        except Exception as e:
            logger.error(f"Error during enrollment: {e}", exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details("Internal error during enrollment")
            return biometrics_pb2.EnrollResponse(
                success=False,
                message=f"Enrollment failed: {str(e)}",
            )

    def VerifyUser(self, request_iterator, context):
        """
        Verify a user against their enrolled voice sample.

        The first message should contain the user_id, followed by
        messages containing audio chunks.
        """
        user_id = None
        audio_chunks = []

        try:
            for request in request_iterator:
                data_type = request.WhichOneof("data")

                if data_type == "user_id":
                    if user_id is not None:
                        logger.warning("Received multiple user_id messages")
                    user_id = request.user_id
                    logger.info(f"Verifying user: {user_id}")
                elif data_type == "audio_chunk":
                    audio_chunks.append(request.audio_chunk)
                else:
                    logger.warning(f"Received empty message")

            if user_id is None:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("No user_id provided in the stream")
                return biometrics_pb2.VerifyResponse(
                    is_match=False,
                    similarity_score=0.0
                )

            if not audio_chunks:
                context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
                context.set_details("No audio data provided in the stream")
                return biometrics_pb2.VerifyResponse(
                    is_match=False,
                    similarity_score=0.0
                )

            # Check if user is enrolled
            if not self.embedding_store.exists(user_id):
                logger.warning(f"User {user_id} is not enrolled")
                context.set_code(grpc.StatusCode.NOT_FOUND)
                context.set_details(f"User {user_id} is not enrolled")
                return biometrics_pb2.VerifyResponse(
                    is_match=False,
                    similarity_score=0.0,
                )

            # Load stored embedding
            stored_embedding = self.embedding_store.load(user_id)

            # Combine all audio chunks
            combined_audio = b"".join(audio_chunks)
            logger.info(f"Received {len(combined_audio)} bytes of audio for verification")

            # Convert to waveform
            waveform = self._bytes_to_waveform(combined_audio)

            # Compute embedding for incoming audio
            incoming_embedding = self._compute_embedding(waveform)

            # Calculate cosine similarity
            cos_sim = torch.nn.CosineSimilarity(dim=0)
            similarity = cos_sim(stored_embedding, incoming_embedding).item()

            is_match = similarity > SIMILARITY_THRESHOLD
            logger.info(
                f"Verification for user {user_id}: "
                f"similarity={similarity:.4f}, threshold={SIMILARITY_THRESHOLD}, match={is_match}"
            )

            return biometrics_pb2.VerifyResponse(
                is_match=is_match,
                similarity_score=similarity
            )

        except ValueError as e:
            logger.error(f"Validation error during verification: {e}", exc_info=True)
            context.set_code(grpc.StatusCode.INVALID_ARGUMENT)
            context.set_details(str(e))
            return biometrics_pb2.VerifyResponse(
                is_match=False,
                similarity_score=0.0,
            )
        except FileNotFoundError as e:
            logger.error(f"Embedding not found during verification: {e}", exc_info=True)
            context.set_code(grpc.StatusCode.NOT_FOUND)
            context.set_details(str(e))
            return biometrics_pb2.VerifyResponse(
                is_match=False,
                similarity_score=0.0,
            )
        except Exception as e:
            logger.error(f"Error during verification: {e}", exc_info=True)
            context.set_code(grpc.StatusCode.INTERNAL)
            context.set_details("Internal error during verification")
            return biometrics_pb2.VerifyResponse(
                is_match=False,
                similarity_score=0.0,
            )


class _HealthHandler(BaseHTTPRequestHandler):
    """Minimal HTTP handler for health checks."""

    def do_GET(self):
        if self.path in ("/", "/health", "/healthz"):
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(b'{"status": "ok"}')
            return

        self.send_response(404)
        self.end_headers()


    def log_message(self, format, *args):  # noqa: A003
        logger.debug("HTTP health check: " + format, *args)


def serve():
    """Start the gRPC server."""
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=1))
    biometrics_pb2_grpc.add_BiometricServiceServicer_to_server(
        BiometricServiceServicer(), server
    )
    health_pb2_grpc.add_HealthServicer_to_server(
        health.HealthServicer(), server
    )
    http_server = None
    try:
        http_server = HTTPServer(("0.0.0.0", HTTP_HEALTH_PORT), _HealthHandler)
    except OSError as exc:
        logger.error(f"Failed to start HTTP health server on port {HTTP_HEALTH_PORT}: {exc}")
        sys.exit(1)

    threading.Thread(target=http_server.serve_forever, daemon=True).start()
    logger.info(f"HTTP health server started on port {HTTP_HEALTH_PORT}")

    server.add_insecure_port(f"[::]:{GRPC_PORT}")
    server.start()
    logger.info(f"Voice Biometrics gRPC server started on port {GRPC_PORT}")

    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        logger.info("Shutting down server...")
        server.stop(grace=5)
        if http_server:
            http_server.shutdown()
            logger.info("HTTP health server stopped")


if __name__ == "__main__":
    serve()

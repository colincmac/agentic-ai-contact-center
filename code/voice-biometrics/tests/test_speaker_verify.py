import os
import sys
from concurrent import futures
from pathlib import Path

import grpc
import pytest
import torch
from speechbrain.dataio import audio_io


from models import biometrics_pb2_grpc, biometrics_pb2

from server import BiometricServiceServicer, SAMPLE_RATE


CHUNK_SIZE = 4096


def _load_pcm_bytes(audio_path: Path) -> bytes:
	"""Load an audio file and convert it to 16-bit PCM bytes at SAMPLE_RATE.

	The server expects raw 16-bit mono PCM at 16 kHz.
	"""

	waveform, sample_rate = audio_io.load(str(audio_path))

	# Convert to mono if necessary (channels x time)
	if waveform.dim() == 2 and waveform.size(0) > 1:
		waveform = waveform.mean(dim=0, keepdim=True)

	# Ensure 1D tensor (time,)
	waveform = waveform.squeeze(0)

	# Clamp to [-1, 1] then convert to 16-bit PCM
	waveform = waveform.clamp(-1.0, 1.0)
	int16_tensor = (waveform * 32767.0).to(torch.int16).cpu()
	return int16_tensor.numpy().tobytes()


def _enroll_request_iterator(user_id: str, audio_bytes: bytes):
	"""Yield EnrollRequest messages: first user_id, then audio chunks."""

	yield biometrics_pb2.EnrollRequest(user_id=user_id)
	for offset in range(0, len(audio_bytes), CHUNK_SIZE):
		chunk = audio_bytes[offset : offset + CHUNK_SIZE]
		yield biometrics_pb2.EnrollRequest(audio_chunk=chunk)


def _verify_request_iterator(user_id: str, audio_bytes: bytes):
	"""Yield VerifyRequest messages: first user_id, then audio chunks."""

	yield biometrics_pb2.VerifyRequest(user_id=user_id)
	for offset in range(0, len(audio_bytes), CHUNK_SIZE):
		chunk = audio_bytes[offset : offset + CHUNK_SIZE]
		yield biometrics_pb2.VerifyRequest(audio_chunk=chunk)


@pytest.fixture(scope="module")
def grpc_stub():
	"""Start an in-process gRPC server and return a BiometricService stub.

	Uses the real BiometricServiceServicer from server.py so the
	end-to-end behavior (including model inference) is exercised.
	"""

	server = grpc.server(futures.ThreadPoolExecutor(max_workers=1))
	servicer = BiometricServiceServicer()
	biometrics_pb2_grpc.add_BiometricServiceServicer_to_server(servicer, server)

	port = server.add_insecure_port("localhost:0")
	server.start()

	channel = grpc.insecure_channel(f"localhost:{port}")
	stub = biometrics_pb2_grpc.BiometricServiceStub(channel)

	try:
		yield stub
	finally:
		server.stop(grace=None)
		channel.close()


@pytest.fixture(scope="module")
def sample_audio_bytes():
	"""Return PCM bytes for two different speaker samples.

	The user-provided files are located under tests/samples.
	"""

	base_dir = Path(__file__).parent / "samples"
	sample1_path = base_dir / "example1.wav"
	sample2_path = base_dir / "example2.flac"

	assert sample1_path.exists(), f"Missing sample audio file: {sample1_path}"
	assert sample2_path.exists(), f"Missing sample audio file: {sample2_path}"

	sample1_bytes = _load_pcm_bytes(sample1_path)
	sample2_bytes = _load_pcm_bytes(sample2_path)

	return sample1_bytes, sample2_bytes


def test_speaker_verification_same_vs_different(grpc_stub, sample_audio_bytes):
	"""Enroll a user and verify same vs different speaker samples.

	The similarity for the same-speaker sample should be higher than
	for the different-speaker sample, exercising the full gRPC path.
	"""

	sample1_bytes, sample2_bytes = sample_audio_bytes
	user_id = "test_user_speaker_verification"

	# Enroll the user with the first sample
	enroll_response = grpc_stub.EnrollUser(
		_enroll_request_iterator(user_id=user_id, audio_bytes=sample1_bytes)
	)

	assert enroll_response.success, f"Enrollment failed: {enroll_response.message}"

	# Verify using the same audio
	same_response = grpc_stub.VerifyUser(
		_verify_request_iterator(user_id=user_id, audio_bytes=sample1_bytes)
	)

	# Verify using a different speaker's audio
	different_response = grpc_stub.VerifyUser(
		_verify_request_iterator(user_id=user_id, audio_bytes=sample2_bytes)
	)

	# For a working speaker recognition model, the similarity score
	# for the same-speaker sample should be greater than for a
	# different-speaker sample.
	assert same_response.similarity_score > different_response.similarity_score, (
		"Expected similarity for same-speaker audio to be greater than "
		"similarity for different-speaker audio, but got "
		f"same={same_response.similarity_score}, "
		f"different={different_response.similarity_score}"
	)


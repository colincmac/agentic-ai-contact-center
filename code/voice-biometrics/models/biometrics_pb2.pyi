from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from typing import ClassVar as _ClassVar, Optional as _Optional

DESCRIPTOR: _descriptor.FileDescriptor

class EnrollRequest(_message.Message):
    __slots__ = ("user_id", "audio_chunk")
    USER_ID_FIELD_NUMBER: _ClassVar[int]
    AUDIO_CHUNK_FIELD_NUMBER: _ClassVar[int]
    user_id: str
    audio_chunk: bytes
    def __init__(self, user_id: _Optional[str] = ..., audio_chunk: _Optional[bytes] = ...) -> None: ...

class EnrollResponse(_message.Message):
    __slots__ = ("success", "message")
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    success: bool
    message: str
    def __init__(self, success: bool = ..., message: _Optional[str] = ...) -> None: ...

class VerifyRequest(_message.Message):
    __slots__ = ("user_id", "audio_chunk")
    USER_ID_FIELD_NUMBER: _ClassVar[int]
    AUDIO_CHUNK_FIELD_NUMBER: _ClassVar[int]
    user_id: str
    audio_chunk: bytes
    def __init__(self, user_id: _Optional[str] = ..., audio_chunk: _Optional[bytes] = ...) -> None: ...

class VerifyResponse(_message.Message):
    __slots__ = ("is_match", "similarity_score")
    IS_MATCH_FIELD_NUMBER: _ClassVar[int]
    SIMILARITY_SCORE_FIELD_NUMBER: _ClassVar[int]
    is_match: bool
    similarity_score: float
    def __init__(self, is_match: bool = ..., similarity_score: _Optional[float] = ...) -> None: ...

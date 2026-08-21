import torch


# Check if GPU is available
if torch.cuda.is_available():
    device = torch.cuda.current_device()
    print("Current device ID:", device)
    print("Current device name:", torch.cuda.get_device_name(device))
    print("Device memory address:", torch.cuda.device(device))
    print("Total number of GPUs:", torch.cuda.device_count())
else:
    device = torch.device("cpu")
    print("❌ GPU not available, using CPU.")
# Create random tensors on the chosen device
a = torch.randn(5000, 5000, device=device)
b = torch.randn(5000, 5000, device=device)
# Perform matrix multiplication (to stress GPU)
print("Performing matrix multiplication...")
result = torch.mm(a, b)
print("Done. Result shape:", result.shape)

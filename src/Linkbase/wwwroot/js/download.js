// Saves a file the server made, such as an export, through the browser's own download.
export async function saveStream(fileName, contentType, streamRef) {
    const bytes = await streamRef.arrayBuffer();
    saveBlob(fileName, new Blob([bytes], { type: contentType }));
}

export function saveBlob(fileName, blob) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

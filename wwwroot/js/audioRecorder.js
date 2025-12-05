let mediaRecorder = null;
let audioChunks = [];
let dotNetHelper = null;
let recordedBlobs = [];
let audioContext = null;
let processor = null;
let streamReference = null;
let recordingFileName = null;

window.startRecording = async (dotNetReference) => {
    console.log('Starting recording...');
    dotNetHelper = dotNetReference;
    audioChunks = [];
    recordedBlobs = [];
    recordingFileName = `recording_${new Date().getTime()}.webm`;

    try {
        const stream = await navigator.mediaDevices.getUserMedia({ 
            audio: {
                channelCount: 1,
                sampleRate: 16000,
                echoCancellation: true,
                noiseSuppression: true
            } 
        });
        
        streamReference = stream;
        console.log('Microphone access granted');

        // Create AudioContext for processing
        audioContext = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 16000 });
        const source = audioContext.createMediaStreamSource(stream);
        processor = audioContext.createScriptProcessor(4096, 1, 1);

        source.connect(processor);
        processor.connect(audioContext.destination);

        let chunkCount = 0;
        processor.onaudioprocess = async (e) => {
            const inputData = e.inputBuffer.getChannelData(0);
            const pcmData = convertFloat32ToInt16(inputData);
            
            chunkCount++;
            if (chunkCount % 50 === 0) {
                console.log(`Sent ${chunkCount} audio chunks`);
            }
            
            try {
                // Convert Int16Array to regular array for .NET
                const byteArray = new Uint8Array(pcmData.buffer);
                const intArray = Array.from(byteArray);
                await dotNetHelper.invokeMethodAsync('ReceiveAudioData', intArray);
            } catch (err) {
                console.error('Error sending audio data:', err);
            }
            
            // Store for backup recording
            recordedBlobs.push(pcmData);
        };

        // Also record using MediaRecorder as backup
        // Use the AudioContext's destination as the source to ensure same processing
        const recordDest = audioContext.createMediaStreamDestination();
        source.connect(recordDest);
        
        mediaRecorder = new MediaRecorder(recordDest.stream);
        mediaRecorder.ondataavailable = (e) => {
            if (e.data && e.data.size > 0) {
                audioChunks.push(e.data);
                console.log(`MediaRecorder chunk: ${e.data.size} bytes, type: ${e.data.type}`);
            }
        };
        
        mediaRecorder.onerror = (e) => {
            console.error('MediaRecorder error:', e);
        };
        
        mediaRecorder.start(1000); // Record in 1 second chunks
        
        console.log('Recording started');
        return recordingFileName; // 録音ファイル名を返す
    } catch (error) {
        console.error('Error accessing microphone:', error);
        alert('マイクへのアクセスに失敗しました: ' + error.message);
        return null;
    }
};

window.stopRecording = () => {
    console.log('Stopping recording...');
    
    // Stop audio processing
    if (processor) {
        processor.disconnect();
        processor = null;
    }
    
    if (audioContext) {
        audioContext.close();
        audioContext = null;
    }
    
    if (streamReference) {
        streamReference.getTracks().forEach(track => track.stop());
        streamReference = null;
    }
    
    if (mediaRecorder && mediaRecorder.state !== 'inactive') {
        mediaRecorder.stop();
        
        // Save backup recording locally (browser only)
        setTimeout(() => {
            const blob = new Blob(audioChunks, { type: 'audio/webm' });
            console.log(`Recording blob created: ${blob.size} bytes`);
            
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.style.display = 'none';
            a.href = url;
            a.download = recordingFileName;
            document.body.appendChild(a);
            a.click();
            URL.revokeObjectURL(url);
            console.log(`Backup recording saved: ${recordingFileName}`);
        }, 100);
    }
    
    console.log('Recording stopped');
};

function convertFloat32ToInt16(float32Array) {
    const int16Array = new Int16Array(float32Array.length);
    for (let i = 0; i < float32Array.length; i++) {
        const s = Math.max(-1, Math.min(1, float32Array[i]));
        int16Array[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
    }
    return int16Array;
}

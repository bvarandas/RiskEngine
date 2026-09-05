using ServiceDefaults.events;

namespace ServiceDefaults.interfaces;

public interface IFastRingBuffer
{
    long NextSequence();
    ref OrderEvent Get(long sequence);
    void Publish(long sequence);

    ref long GetProducerSequence();
}

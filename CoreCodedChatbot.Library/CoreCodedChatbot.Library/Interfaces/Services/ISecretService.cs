using System.Threading.Tasks;

namespace CoreCodedChatbot.Library.Interfaces.Services
{
    public interface ISecretService
    {
        Task Initialize();

        T GetSecret<T>(string secretKey);
    }
}
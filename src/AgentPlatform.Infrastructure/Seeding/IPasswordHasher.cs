namespace AgentPlatform.Infrastructure.Seeding;

/// <summary>
/// Хеширование паролей пользователей кабинета.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Возвращает строку хеша для сохранения в users.password_hash.</summary>
    string Hash(string password);

    /// <summary>Проверяет пароль против сохранённого хеша.</summary>
    bool Verify(string password, string passwordHash);
}

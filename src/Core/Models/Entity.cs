

namespace Core.Models
{
    public class Entity
    {
        public virtual int Id { get; set; }
        public virtual string Name { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string DeactivatedBy { get; set; }
        public DateTime? DeactivatedAt { get; set; }
        public bool IsActive { get; set; }
    }

}
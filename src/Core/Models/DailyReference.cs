using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Core.Models
{
    /// <summary>
    /// Represents a daily reference entity that contains information about references related to daily entries.
    /// </summary>
    public class DailyReference : Entity
    {
        /// <summary>
        /// Gets or sets the ID of the daily entry this reference belongs to.
        /// This property is required and serves as a foreign key to the Daily entity.
        /// </summary>
        [Required]
        public int DailyId { get; set; }
        /// <summary>
        /// Gets or sets the related daily entry.
        /// This represents the navigation property to the Daily entity.
        /// </summary>
        public Daily Daily { get; set; }
        /// <summary>
        /// Gets or sets the file path or URL of the reference.
        /// This property is required and stores the location of the reference material.
        /// </summary>
        [Required]
        public string ReferencePath { get; set; }
        /// <summary>
        /// Gets or sets the name of the reference.
        /// This property is not mapped to the database and is used for display purposes only.
        /// </summary>
        [NotMapped]
        public override string Name { get; set; }
        /// <summary>
        /// Gets or sets the description of the reference.
        /// This provides additional context or details about the reference.
        /// </summary>
        public string Description { get; set; }

    }
}
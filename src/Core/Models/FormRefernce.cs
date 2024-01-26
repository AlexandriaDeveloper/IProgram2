using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Threading.Tasks;

namespace Core.Models
{
    public class FormRefernce : Entity
    {
        [NotMapped]
        public override string Name { get => base.Name; set => base.Name = value; }
        public int FormId { get; set; }
        [MaxLength(250)]
        public string ReferencePath { get; set; }
        public Form Form { get; set; }

    }
}